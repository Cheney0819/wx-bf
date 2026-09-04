using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Footprint.Media;
using Footprint.Parser;

namespace Footprint.Archive;

public sealed class LocalArchivePipeline(Action<ArchiveBuildStage>? stageObserved = null)
{
    public Task<ArchiveArtifact> BuildAsync(string verifiedExpandedPackageRoot, string archiveRoot,
        string scratchRoot, CancellationToken cancellationToken = default) =>
        BuildCoreAsync(verifiedExpandedPackageRoot, archiveRoot, scratchRoot, null, null, cancellationToken);

    public Task<ArchiveArtifact> BuildAsync(string verifiedExpandedPackageRoot, string archiveRoot,
        string scratchRoot, string expectedSourceDeviceId, CancellationToken cancellationToken = default)
    {
        if (!ExpandedPackageContract.IsIdentifier(expectedSourceDeviceId))
            throw new ArgumentException("预期来源 DeviceId 无效。", nameof(expectedSourceDeviceId));
        return BuildCoreAsync(verifiedExpandedPackageRoot, archiveRoot, scratchRoot, expectedSourceDeviceId, null,
            cancellationToken);
    }

    public Task<ArchiveArtifact> BuildAsync(string verifiedExpandedPackageRoot, string archiveRoot,
        string scratchRoot, string expectedSourceDeviceId, string expectedManifestRunId,
        CancellationToken cancellationToken = default)
    {
        if (!ExpandedPackageContract.IsIdentifier(expectedSourceDeviceId))
            throw new ArgumentException("预期来源 DeviceId 无效。", nameof(expectedSourceDeviceId));
        return BuildCoreAsync(verifiedExpandedPackageRoot, archiveRoot, scratchRoot, expectedSourceDeviceId,
            expectedManifestRunId, cancellationToken);
    }

    private async Task<ArchiveArtifact> BuildCoreAsync(string verifiedExpandedPackageRoot, string archiveRoot,
        string scratchRoot, string? expectedSourceDeviceId, string? expectedManifestRunId,
        CancellationToken cancellationToken)
    {
        var expanded = Path.GetFullPath(verifiedExpandedPackageRoot);
        ValidateExpandedRoot(expanded);
        var manifest = await ExpandedPackageContract.LoadAsync(expanded, cancellationToken).ConfigureAwait(false);
        if (expectedManifestRunId is not null &&
            !string.Equals(manifest.RunId, expectedManifestRunId, StringComparison.Ordinal))
            throw new InvalidDataException("接收包 Manifest RunId 与待接收 RunId 不一致。");
        if (expectedSourceDeviceId is not null &&
            !string.Equals(manifest.DeviceId, expectedSourceDeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("接收包 Manifest DeviceId 与认证来源不一致。");
        var sourceId = manifest.RunId;
        var operationScratch = Path.Combine(Path.GetFullPath(scratchRoot), sourceId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationScratch);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(operationScratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            ValidateDatabaseGroups(manifest.Entries);
            var sources = await SnapshotVerifiedSourcesAsync(manifest.Entries, expanded,
                Path.Combine(operationScratch, "verified-package"), cancellationToken).ConfigureAwait(false);
            var databaseGroups = sources.Where(source => source.SourceCategory == "database")
                .GroupBy(source => source.SourceIdentityHash, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
            if (databaseGroups.Length == 0) throw new InvalidDataException("接收包未包含数据库清单组。");
            var stableNamespaces = ResolveStableNamespaces(databaseGroups);
            var parsedGroups = new List<ParsedPackage>();
            var logicalRows = sources.Where(source => source.SourceCategory == "decompression" &&
                    string.Equals(Path.GetFileName(source.RelativePath), "logical-rows.jsonl", StringComparison.Ordinal))
                .OrderBy(source => source.RelativePath, StringComparer.Ordinal).ToArray();
            var tablePreview = sources.FirstOrDefault(source => source.SourceCategory == "decompression" &&
                string.Equals(Path.GetFileName(source.RelativePath), "table-preview.jsonl", StringComparison.Ordinal));
            var recordSources = sources.Where(source => source.SourceCategory == "decompression" &&
                source.RelativePath.StartsWith("Footprint_Decompression/records/", StringComparison.Ordinal)).ToArray();
            if (logicalRows.Length > 0 || tablePreview is not null)
            {
                var logicalNamespaces = stableNamespaces.Values.Distinct(StringComparer.Ordinal).ToArray();
                if (logicalNamespaces.Length != 1)
                    throw new InvalidDataException("逻辑行跨越多个账户命名空间。");
                var recordsRoot = Path.Combine(operationScratch, "verified-package", "Footprint_Decompression", "records");
                var databaseIdentityByName = databaseGroups.SelectMany(group => group
                        .Where(value => !value.RelativePath.EndsWith("-wal", StringComparison.Ordinal) &&
                                        !value.RelativePath.EndsWith("-shm", StringComparison.Ordinal)))
                    .ToDictionary(value => Path.GetFileName(value.RelativePath), value =>
                        databaseGroups.Single(group => group.Key == value.SourceIdentityHash).Key,
                        StringComparer.Ordinal);
                parsedGroups.Add(await new LogicalRowsExporter().ExportAsync(
                    logicalRows.Select(source => source.SourcePath).ToArray(),
                    databaseGroups.Select(group => group.Key).ToArray(), logicalNamespaces[0], sourceId,
                    tablePreview?.SourcePath, recordSources.Length > 0 ? recordsRoot : null,
                    databaseIdentityByName,
                    cancellationToken).ConfigureAwait(false));
            }
            var exporter = new DatabaseExporter(new DatabaseExportOptions(500,
                Path.Combine(operationScratch, "database")));
            foreach (var group in databaseGroups)
            {
                if (!await HasPlainSqliteHeaderAsync(group, cancellationToken).ConfigureAwait(false)) continue;
                var files = group.OrderBy(value => value.RelativePath, StringComparer.Ordinal)
                    .Select(value => new DatabaseSourceFile(value.SourcePath, Path.GetFileName(value.RelativePath),
                        value.Length, value.Sha256)).ToArray();
                parsedGroups.Add(await exporter.ExportAsync(new DatabaseSourceGroup(stableNamespaces[group.Key],
                    group.Key, files), sourceId, cancellationToken).ConfigureAwait(false));
            }
            var parsed = MergeParsed(sourceId, parsedGroups);
            parsed = WeixinMediaAssociator.Attach(parsed, sources
                .Where(source => source.SourceCategory is "image" or "voice" or "attachment")
                .Select(source => new CapturedMediaReference(source.RelativePath, source.Sha256,
                    source.SourceCategory, source.AssociationEvidence)).ToArray());
            if (parsed.Contacts.Count + parsed.Sessions.Count + parsed.Messages.Count + parsed.Favorites.Count == 0)
                throw new InvalidDataException("数据库组未产生受支持的本地内容记录。");
            var sourceMap = sources.ToDictionary(source => source.RelativePath, StringComparer.Ordinal);
            var transformed = new List<ArchiveMediaInput>();
            foreach (var media in parsed.Media.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                if (!sourceMap.TryGetValue(media.RelativePath, out var source) || source.Sha256 != media.Sha256)
                    throw new InvalidDataException("数据库媒体引用与接收包清单不匹配。");
                var outputRoot = Path.Combine(operationScratch, "media");
                MediaTransformResult result = media.Kind switch
                {
                    "image" => await new ImageDecryptor().DecryptAsync(source.SourcePath, source.Sha256, outputRoot, cancellationToken).ConfigureAwait(false),
                    "voice" => await new VoiceTransformer().ToWaveAsync(source.SourcePath, source.Sha256, outputRoot, 8000, 1, cancellationToken).ConfigureAwait(false),
                    _ => await new VerifiedMediaCopier().CopyAsync(source.SourcePath, source.Sha256, outputRoot, Path.GetExtension(source.RelativePath) is { Length: > 1 } extension ? extension : ".bin", cancellationToken).ConfigureAwait(false)
                };
                transformed.Add(new ArchiveMediaInput(media.Id, media.MessageId, media.Kind, result.OutputPath,
                    result.OutputSha256, result.Format));
            }
            return await new ArchiveBuilder(stageObserved).BuildAsync(parsed, transformed, archiveRoot,
                manifest.DeviceId, cancellationToken).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(operationScratch)) Directory.Delete(operationScratch, true); }
    }

    private static ParsedPackage MergeParsed(string sourceId, IReadOnlyList<ParsedPackage> packages) => new(sourceId,
        Merge(packages.SelectMany(value => value.Contacts), value => value.Id),
        Merge(packages.SelectMany(value => value.Sessions), value => value.Id),
        Merge(packages.SelectMany(value => value.Messages), value => value.Id),
        Merge(packages.SelectMany(value => value.Favorites), value => value.Id),
        Merge(packages.SelectMany(value => value.Media), value => value.Id));

    private static IReadOnlyDictionary<string, string> ResolveStableNamespaces(
        IReadOnlyList<IGrouping<string, ExpandedPackageSource>> groups)
    {
        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var identities = group.SelectMany(value => value.AssociationEvidence)
                .Where(value => value.Key == "account_identity").Select(value => value.Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length > 1 || identities.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("数据库组账户身份声明冲突。");
            declared.Add(group.Key, identities.SingleOrDefault());
        }
        if (declared.Values.Any(value => value is not null))
        {
            if (declared.Values.Any(value => value is null))
                throw new InvalidDataException("数据库组账户身份声明不完整。");
            return declared.ToDictionary(value => value.Key, value => value.Value!, StringComparer.Ordinal);
        }

        var canonical = "receiver-database-set-v1\0" + string.Join("\n", groups.Select(value => value.Key));
        var fallback = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return groups.ToDictionary(value => value.Key, _ => fallback, StringComparer.Ordinal);
    }

    private static IReadOnlyList<T> Merge<T>(IEnumerable<T> values, Func<T, string> id) where T : notnull
    {
        var result = new List<T>();
        foreach (var group in values.GroupBy(id, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var distinct = group.Distinct().ToArray();
            if (distinct.Length != 1) throw new InvalidDataException("多个数据库组产生了冲突的实体记录。");
            result.Add(distinct[0]);
        }
        return result;
    }

    private static void ValidateDatabaseGroups(IReadOnlyList<ExpandedPackageSource> entries)
    {
        foreach (var group in entries.Where(value => value.SourceCategory == "database").GroupBy(value => value.SourceIdentityHash, StringComparer.Ordinal))
        {
            var primary = group.Where(value => !value.RelativePath.EndsWith("-wal", StringComparison.Ordinal) && !value.RelativePath.EndsWith("-shm", StringComparison.Ordinal)).ToArray();
            if (primary.Length != 1) throw new InvalidDataException("数据库清单组必须包含一个主数据库。");
            var names = group.Select(value => value.RelativePath).ToHashSet(StringComparer.Ordinal);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = primary[0].SourcePath + suffix;
                if (File.Exists(sidecar) && !names.Contains(primary[0].RelativePath + suffix))
                    throw new InvalidDataException("数据库 sidecar 未由接收包清单声明。");
            }
        }
    }

    private static async Task<bool> HasPlainSqliteHeaderAsync(
        IGrouping<string, ExpandedPackageSource> group, CancellationToken cancellationToken)
    {
        var primary = group.Single(value => !value.RelativePath.EndsWith("-wal", StringComparison.Ordinal) &&
                                            !value.RelativePath.EndsWith("-shm", StringComparison.Ordinal));
        if (primary.Length < 16) return false;
        var header = new byte[16];
        await using var stream = new FileStream(primary.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task<IReadOnlyList<ExpandedPackageSource>> SnapshotVerifiedSourcesAsync(
        IReadOnlyList<ExpandedPackageSource> sources, string expandedRoot, string snapshotRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(snapshotRoot);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshotRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = new List<ExpandedPackageSource>(sources.Count);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var source in sources.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(source.SourcePath); info.Refresh();
                RejectIntermediateLinks(source.SourcePath, expandedRoot);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != source.Length)
                    throw new InvalidDataException("接收包展开文件长度或类型无效。");
                var destination = Path.Combine(snapshotRoot, source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length = checked(length + read);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (length != source.Length || actual != source.Sha256) throw new InvalidDataException("接收包展开文件 SHA-256 校验失败。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead);
                result.Add(source with { SourcePath = destination });
            }
            return result;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    private static void RejectIntermediateLinks(string path, string boundaryRoot)
    {
        var current = new FileInfo(path).Directory;
        while (current is not null)
        {
            current.Refresh();
            if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("接收包展开路径包含链接。");
            if (string.Equals(current.FullName, boundaryRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = current.Parent;
        }
    }

    private static void ValidateExpandedRoot(string root)
    {
        var info = new DirectoryInfo(root); info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("必须使用已验证的普通接收包展开目录。");
    }
}

internal sealed record ExpandedPackageSource(string RelativePath, string SourcePath, long Length, string Sha256,
    string SourceCategory, string SourceIdentityHash, IReadOnlyDictionary<string, string> AssociationEvidence);

internal sealed record ExpandedPackageManifest(string RunId, string DeviceId,
    IReadOnlyList<ExpandedPackageSource> Entries);

internal static class ExpandedPackageContract
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<ExpandedPackageManifest> LoadAsync(string root, CancellationToken cancellationToken)
    {
        var manifestPath = Resolve(root, "Footprint_CaptureManifest.json");
        var info = new FileInfo(manifestPath); info.Refresh();
        if (!info.Exists || info.Length is <= 0 or > MaximumManifestBytes || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("接收包清单无效。");
        byte[] bytes = new byte[checked((int)info.Length)];
        await using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        ManifestDto dto;
        try { dto = JsonSerializer.Deserialize<ManifestDto>(bytes, JsonOptions) ?? throw new InvalidDataException("接收包清单为空。"); }
        catch (JsonException error) { throw new InvalidDataException("接收包清单 JSON 无效。", error); }
        ValidateRunId(dto.RunId);
        if (dto.Schema != "footprint.capture-manifest.v1" || !IsIdentifier(dto.DeviceId) ||
            dto.CaptureGeneration < 1 || dto.CreatedAtUtc.Offset != TimeSpan.Zero || dto.Entries is null)
            throw new InvalidDataException("接收包清单契约无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ExpandedPackageSource>(dto.Entries.Count);
        foreach (var entry in dto.Entries)
        {
            if (entry is null) throw new InvalidDataException("接收包清单条目为空。");
            var relative = Normalize(entry.RelativePath);
            var prefix = entry.SourceCategory switch
            {
                "database" => "Footprint_Databases/",
                "decompression" => "Footprint_Decompression/",
                "image" => "Footprint_MediaSnapshot/image/",
                "voice" => "Footprint_MediaSnapshot/voice/",
                "favorite" => "Footprint_MediaSnapshot/favorite/",
                "attachment" => "Footprint_MediaSnapshot/attachment/",
                _ => throw new InvalidDataException("接收包清单来源分类无效。")
            };
            if (!relative.StartsWith(prefix, StringComparison.Ordinal) || !paths.Add(relative) || entry.Length < 0 ||
                entry.SnapshotTimestampUtc.Offset != TimeSpan.Zero || entry.StabilityAttempts < 1 || entry.AssociationEvidence is null)
                throw new InvalidDataException("接收包清单条目无效。");
            ValidateSha(entry.Sha256); ValidateSha(entry.SourceIdentityHash);
            entries.Add(new ExpandedPackageSource(relative, Resolve(root, relative), entry.Length, entry.Sha256,
                entry.SourceCategory, entry.SourceIdentityHash, entry.AssociationEvidence));
        }
        return new ExpandedPackageManifest(dto.RunId, dto.DeviceId, entries);
    }

    private static string Resolve(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("接收包清单路径逃逸展开目录。");
        return path;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') || Path.IsPathRooted(value)) throw new InvalidDataException("接收包清单路径无效。");
        var parts = value.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("接收包清单路径无效。");
        return value;
    }

    private static void ValidateRunId(string value)
    {
        if (value is null || value.Length != "Footprint_Run_".Length + 32 || !value.StartsWith("Footprint_Run_", StringComparison.Ordinal) || value["Footprint_Run_".Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("接收包清单 RunId 无效。");
    }
    internal static bool IsIdentifier(string value) => value is { Length: >= 1 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static void ValidateSha(string value) { if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new InvalidDataException("接收包清单摘要无效。"); }

    private sealed record ManifestDto(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("capture_generation")] long CaptureGeneration,
        [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("entries")] IReadOnlyList<EntryDto> Entries);
    private sealed record EntryDto(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("length")] long Length,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("source_category")] string SourceCategory,
        [property: JsonPropertyName("source_identity_hash")] string SourceIdentityHash,
        [property: JsonPropertyName("snapshot_timestamp_utc")] DateTimeOffset SnapshotTimestampUtc,
        [property: JsonPropertyName("stability_attempts")] int StabilityAttempts,
        [property: JsonPropertyName("association_evidence")] IReadOnlyDictionary<string, string> AssociationEvidence);
}
