using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wx411.Core;

public sealed record EvidenceBundleResult(
    string BundlePath,
    EvidenceAssessment Assessment);

public sealed class EvidenceBundleService
{
    private static readonly DateTimeOffset ZipEpoch =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex LabeledKeyHex = new(
        @"(?<prefix>(?:raw[\s_-]*key|key[\s_-]*hex|candidate[\s_-]*key|database[\s_-]*key|候选\s*key|原始\s*key|\bkey\b)[\""']?\s*[:=：]\s*[\""']?)(?<secret>[0-9a-f]{96}|[0-9a-f]{64})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<EvidenceBundleResult> ExportAsync(
        EvidenceSessionSnapshot session,
        string windowLog,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(windowLog);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        var assessment = EvidenceGateEvaluator.Assess(session);
        var document = new EvidenceBundleDocument(
            ExportedUtc: DateTimeOffset.UtcNow,
            assessment,
            session);
        entries.Add("evidence.json", Encoding.UTF8.GetBytes(
            Redact(JsonSerializer.Serialize(document, JsonOptions))));
        entries.Add("window-log.txt", Encoding.UTF8.GetBytes(Redact(windowLog)));
        entries.Add("SUMMARY.txt", Encoding.UTF8.GetBytes(
            RenderSummary(session, assessment)));
        entries.Add("SHA256SUMS.txt", Encoding.UTF8.GetBytes(RenderChecksums(entries)));

        var id = Guid.NewGuid().ToString("N");
        var fileName = $"Wx411Easy-evidence-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id[..8]}.zip";
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        var temporaryPath = Path.Combine(destinationDirectory, $".{fileName}.{id}.tmp");
        try
        {
            await WriteZipAsync(temporaryPath, entries, cancellationToken);
            VerifyZip(temporaryPath, entries.Keys);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath);
            return new EvidenceBundleResult(destinationPath, assessment);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return LabeledKeyHex.Replace(
            text,
            match => match.Groups["prefix"].Value + "[REDACTED]");
    }

    private static string RenderSummary(
        EvidenceSessionSnapshot session,
        EvidenceAssessment assessment)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Wx411Easy 验收证据摘要");
        builder.AppendLine("=======================");
        builder.AppendLine();
        builder.AppendLine($"工具版本：{session.ToolVersion}");
        builder.AppendLine($"会话开始 UTC：{session.StartedUtc:O}");
        builder.AppendLine($"操作系统：{session.OsDescription}");
        builder.AppendLine($"64 位进程/系统：{session.Is64BitProcess}/{session.Is64BitOperatingSystem}");
        builder.AppendLine($"操作记录：{session.Operations.Count}");
        builder.AppendLine();
        AppendGate(builder, assessment.GateA);
        AppendGate(builder, assessment.GateB);
        AppendGate(builder, assessment.GateC);
        AppendGate(builder, assessment.GateD);
        builder.AppendLine($"总结果：{StatusText(assessment.Overall)}");
        builder.AppendLine("总结果只由门禁 B（精准捕获）和门禁 C（取消/票据未复用）决定；门禁 D 仅记录源文件变化。");

        builder.AppendLine();
        builder.AppendLine("说明：数据库文件、key、DPAPI 密文和内存内容未打包。数据库仅记录路径、长度、SHA-256 与应用内 integrity_check 结果。");
        return builder.ToString();
    }

    private static void AppendGate(StringBuilder builder, EvidenceGateResult gate) =>
        builder.AppendLine($"门禁 {gate.Gate}：{StatusText(gate.Status)}；{gate.Detail}");

    private static string StatusText(EvidenceGateStatus status) => status switch
    {
        EvidenceGateStatus.Pass => "PASS",
        EvidenceGateStatus.Fail => "FAIL",
        EvidenceGateStatus.Incomplete => "INCOMPLETE",
        EvidenceGateStatus.NotApplicable => "N/A",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string RenderChecksums(IReadOnlyDictionary<string, byte[]> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var digest = SHA256.HashData(entry.Value);
            try
            {
                builder.Append(Convert.ToHexString(digest).ToLowerInvariant());
                builder.Append("  ");
                builder.Append(entry.Key);
                builder.Append('\n');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        return builder.ToString();
    }

    private static async Task WriteZipAsync(
        string path,
        IReadOnlyDictionary<string, byte[]> entries,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(item.Key, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipEpoch;
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(item.Value, cancellationToken);
        }
    }

    private static void VerifyZip(string path, IEnumerable<string> expectedNames)
    {
        using var archive = ZipFile.OpenRead(path);
        var expected = expectedNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var actual = archive.Entries.Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidDataException("Evidence ZIP entry set does not match the prepared evidence set.");

        foreach (var entry in archive.Entries)
        {
            if (HasForbiddenPayloadExtension(entry.FullName))
                throw new InvalidDataException("Evidence ZIP contains a forbidden payload: " + entry.FullName);
            using var source = entry.Open();
            source.CopyTo(Stream.Null);
        }
    }

    private static bool HasForbiddenPayloadExtension(string name) =>
        name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".capture", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of an unpublished evidence temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // The primary export result remains authoritative when cleanup is denied.
        }
    }

    private sealed record EvidenceBundleDocument(
        DateTimeOffset ExportedUtc,
        EvidenceAssessment Assessment,
        EvidenceSessionSnapshot Session);
}
