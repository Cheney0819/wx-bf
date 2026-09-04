using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Worker;

internal sealed class FridaWcdbDecompressionAdapter(CapturePipelineState state) : IWcdbDecompressionPort
{
    private static readonly string[] DeclaredPathProperties =
    [
        "schema_path", "compression_records_path", "records_path", "table_stats_path",
        "table_preview_path", "logical_rows_path", "agent_diagnostics_path", "runtime_probes_path",
        "artifacts_path", "voice_records_path"
    ];

    public async Task<WcdbDecompressionResult> DecompressAsync(CaptureStageContext context,
        string temporaryDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var input = state.GetDecompressionInput();
            var result = await input.Session.DecompressAsync(input.Bindings, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccessful)
                return WcdbDecompressionResult.Failure(result.Code, result.MessageZh);
            var expectedOutput = Path.Combine(input.Session.OutputDirectory, "runtime-export");
            if (!PathsEqual(result.OutputDirectory, expectedOutput) || !Directory.Exists(expectedOutput))
                return Invalid();

            var summaryPath = Path.Combine(expectedOutput, "decompression-summary.json");
            if (!File.Exists(summaryPath)) return Invalid();
            if ((File.GetAttributes(expectedOutput) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateDirectories(expectedOutput, "*", SearchOption.AllDirectories)
                    .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                return Invalid();
            var summaryBytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            var expectedBindings = input.Bindings.Select(binding => new DecompressionSummaryBinding(binding.Path,
                binding.Tag, binding.KeySha256, binding.KeyLength)).ToArray();
            var summaryValidation = DecompressionSummaryValidator.Validate(summaryBytes, expectedBindings);
            if (!summaryValidation.IsValid)
                return WcdbDecompressionResult.Failure(
                    DecompressionSummaryValidator.StageFailureCode(summaryValidation.Code),
                    summaryValidation.MessageZh);
            var summary = summaryValidation.Summary;

            if (!TryBuildAllowlist(expectedOutput, summaryPath, summary, out var allowlist)) return Invalid();
            if (!MatchesActualFileSet(expectedOutput, allowlist.Keys)) return Invalid();
            Directory.CreateDirectory(temporaryDirectory);
            var artifacts = new List<CaptureStageArtifact>(allowlist.Count);
            long totalBytes = 0;
            foreach (var item in allowlist.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = item.Key;
                var source = ResolveRuntimeExportPath(expectedOutput, relative);
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) return InvalidAndClean();
                if (!await MatchesFingerprintAsync(source, item.Value, cancellationToken).ConfigureAwait(false))
                    return InvalidAndClean();
                var destination = Path.Combine(temporaryDirectory,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                var snapshot = await StableSnapshotter.CreateFileAsync(source, destination, 3,
                    cancellationToken).ConfigureAwait(false);
                if (!snapshot.Stable) return InvalidAndClean();
                if (item.Value is not null && (snapshot.Size != item.Value.Length ||
                    !string.Equals(snapshot.Sha256, item.Value.Sha256, StringComparison.OrdinalIgnoreCase)))
                    return InvalidAndClean();
                if (!await MatchesFingerprintAsync(destination,
                        new OutputFingerprint(snapshot.Size, snapshot.Sha256), cancellationToken)
                    .ConfigureAwait(false)) return InvalidAndClean();
                totalBytes = checked(totalBytes + snapshot.Size);
                artifacts.Add(new CaptureStageArtifact(relative,
                    $"Footprint_Decompression/{relative}", snapshot.Size, snapshot.Sha256,
                    CaptureSourceCategory.Decompression,
                    Hashing.Sha256Hex(Encoding.UTF8.GetBytes(relative)), snapshot.StabilityAttempts,
                    new Dictionary<string, string>()));
            }

            if (!MatchesActualFileSet(expectedOutput, allowlist.Keys)) return InvalidAndClean();
            foreach (var artifact in artifacts)
            {
                var source = ResolveRuntimeExportPath(expectedOutput, artifact.TemporaryRelativePath);
                if (!await MatchesFingerprintAsync(source,
                        new OutputFingerprint(artifact.Length, artifact.Sha256), cancellationToken)
                    .ConfigureAwait(false)) return InvalidAndClean();
            }

            var metrics = CollectMetrics(summary, artifacts.Count, totalBytes);
            return WcdbDecompressionResult.Success(result.Code, result.MessageZh, artifacts, metrics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or OverflowException)
        {
            CleanDirectory(temporaryDirectory);
            return Invalid();
        }

        WcdbDecompressionResult InvalidAndClean()
        {
            CleanDirectory(temporaryDirectory);
            return Invalid();
        }
    }

    private static bool TryBuildAllowlist(string runtimeExport, string summaryPath, JsonElement summary,
        out IReadOnlyDictionary<string, OutputFingerprint?> allowlist)
    {
        allowlist = new Dictionary<string, OutputFingerprint?>();
        var files = new Dictionary<string, OutputFingerprint?>(PathComparer());
        try
        {
            var summaryRelative = NormalizeRelative(runtimeExport, summaryPath);
            files.Add(summaryRelative, null);

            var hasExactOutputs = summary.TryGetProperty("output_files", out var outputFiles);
            if (hasExactOutputs && outputFiles.ValueKind != JsonValueKind.Array) return Failure();

            var declaredRoots = new List<(string Path, string PropertyName)>();
            foreach (var propertyName in DeclaredPathProperties)
            {
                if (!summary.TryGetProperty(propertyName, out var pathElement) ||
                    pathElement.ValueKind == JsonValueKind.Null) continue;
                if (pathElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(pathElement.GetString())) return Failure();
                declaredRoots.Add((NormalizeDeclaredRelative(pathElement.GetString()!), propertyName));
            }
            if (declaredRoots.Count == 0 && summary.TryGetProperty("record_counts", out _))
                declaredRoots.Add(("records", "records_path"));

            var verifiedDeclaredRoots = new List<string>();
            foreach (var (declared, propertyName) in declaredRoots)
            {
                var path = ResolveRuntimeExportPath(runtimeExport, declared);
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Failure();
                    var relative = NormalizeRelative(runtimeExport, path);
                    verifiedDeclaredRoots.Add(relative);
                    if (!hasExactOutputs) files.TryAdd(relative, null);
                }
                else if (Directory.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return Failure();
                    verifiedDeclaredRoots.Add(NormalizeRelative(runtimeExport, path));
                    if (!hasExactOutputs)
                    {
                        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return Failure();
                            files.TryAdd(NormalizeRelative(runtimeExport, file), null);
                        }
                    }
                }
                else if (!CanOmitMissingDeclaredPath(summary, propertyName)) return Failure();
            }

            if (hasExactOutputs)
            {
                foreach (var item in outputFiles.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        item.EnumerateObject().Any(property => property.Name is not "path" and not "length" and
                            not "sha256") ||
                        !TryString(item, "path", out var path) ||
                        !item.TryGetProperty("length", out var lengthElement) ||
                        !lengthElement.TryGetInt64(out var length) || length < 0 ||
                        !TryString(item, "sha256", out var sha256) || !IsSha256(sha256)) return Failure();
                    var relative = NormalizeRelative(runtimeExport,
                        ResolveRuntimeExportPath(runtimeExport, path));
                    if (string.Equals(relative, summaryRelative, PathComparison())) continue;
                    if (!IsUnderDeclaredRoot(relative, verifiedDeclaredRoots)) return Failure();
                    if (!files.TryAdd(relative, new OutputFingerprint(length, sha256.ToLowerInvariant())))
                        return Failure();
                }
            }

            if (files.Count == 0) return Failure();
            allowlist = files;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            return Failure();
        }

        bool Failure()
        {
            return false;
        }
    }

    private static bool IsUnderDeclaredRoot(string relative, IEnumerable<string> declaredRoots)
    {
        foreach (var root in declaredRoots)
        {
            if (string.Equals(relative, root, PathComparison()) ||
                relative.StartsWith(root.TrimEnd('/') + "/", PathComparison()))
                return true;
        }
        return false;
    }

    private static bool CanOmitMissingDeclaredPath(JsonElement summary, string propertyName)
    {
        if (propertyName == "agent_diagnostics_path")
            return summary.TryGetProperty("agent_diagnostic_count", out var count) &&
                   count.TryGetInt64(out var diagnosticCount) && diagnosticCount == 0;

        if (propertyName == "runtime_probes_path")
            return summary.TryGetProperty("runtime_probe_count", out var probeCount) &&
                   probeCount.TryGetInt64(out var runtimeProbeCount) && runtimeProbeCount == 0;

        if (propertyName == "compression_records_path")
            return summary.TryGetProperty("compression_record_count", out var compressionRecordCount) &&
                   compressionRecordCount.TryGetInt64(out var recordCount) && recordCount == 0;

        if (propertyName is "artifacts_path" or "voice_records_path")
            return summary.TryGetProperty("media_record_count", out var mediaRecordCount) &&
                   mediaRecordCount.TryGetInt64(out var recordCount) && recordCount == 0;

        if (propertyName != "records_path" ||
            !summary.TryGetProperty("record_counts", out var recordCounts) ||
            recordCounts.ValueKind != JsonValueKind.Object ||
            recordCounts.EnumerateObject().Any(item =>
                !item.Value.TryGetInt64(out var recordCount) || recordCount != 0)) return false;

        return summary.GetProperty("database_results").EnumerateArray().All(result =>
            result.TryGetProperty("records", out var records) &&
            records.TryGetInt64(out var recordCount) && recordCount == 0);
    }

    private static bool MatchesActualFileSet(string runtimeExport, IEnumerable<string> allowed)
    {
        try
        {
            var expected = new HashSet<string>(allowed, PathComparer());
            var actual = new HashSet<string>(PathComparer());
            foreach (var file in Directory.EnumerateFiles(runtimeExport, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                if (!actual.Add(NormalizeRelative(runtimeExport, file))) return false;
            }
            return actual.SetEquals(expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> MatchesFingerprintAsync(string path, OutputFingerprint? expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (expected is null) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expected.Length) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false)).ToLowerInvariant();
        return string.Equals(hash, expected.Sha256, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, long> CollectMetrics(JsonElement summary, int fileCount,
        long totalBytes)
    {
        var metrics = new SortedDictionary<string, long>(StringComparer.Ordinal)
        {
            ["database_count"] = summary.GetProperty("database_results").GetArrayLength(),
            ["output_file_count"] = fileCount,
            ["output_total_bytes"] = totalBytes
        };
        long records = 0;
        foreach (var result in summary.GetProperty("database_results").EnumerateArray())
            records = checked(records + result.GetProperty("records").GetInt64());
        metrics["record_count"] = records;
        return metrics;
    }

    private static bool TryString(JsonElement value, string propertyName, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string NormalizeDeclaredRelative(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (!CaptureWorkspace.IsSafeRelativePath(normalized))
            throw new InvalidDataException("Frida 解压声明路径无效。");
        return normalized;
    }

    private static string ResolveRuntimeExportPath(string runtimeExport, string relativePath)
    {
        var normalized = NormalizeDeclaredRelative(relativePath);
        var root = Path.GetFullPath(runtimeExport).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Frida 解压声明路径超出运行时输出目录。");
        return path;
    }

    private static string NormalizeRelative(string runtimeExport, string path)
    {
        var root = Path.GetFullPath(runtimeExport).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Frida 解压输出路径超出运行时输出目录。");
        return NormalizeDeclaredRelative(Path.GetRelativePath(root, full));
    }

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static WcdbDecompressionResult Invalid() => WcdbDecompressionResult.Failure(
        DecompressionSummaryValidator.InvalidCode, DecompressionSummaryValidator.InvalidMessageZh);

    private static void CleanDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record OutputFingerprint(long Length, string Sha256);
}
