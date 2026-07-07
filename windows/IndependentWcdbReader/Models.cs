using System.Text.Json;
using System.Text.Json.Serialization;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record CliOptions(
    string Mode,
    string AccountDir,
    string SessionDbPath,
    string MessageDbPath,
    string Wxid,
    string Key,
    string OutputJsonPath,
    string OutputSqlitePath
)
{
    public static CliOptions Parse(string[] args)
    {
        string mode = "probe";
        string? accountDir = null;
        string? sessionDbPath = null;
        string? messageDbPath = null;
        string? wxid = null;
        string? key = null;
        string? outputJsonPath = null;
        string? outputSqlitePath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (string.Equals(current, "--mode", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                mode = args[++index].Trim();
                continue;
            }

            if (string.Equals(current, "--account-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                accountDir = args[++index];
                continue;
            }

            if (string.Equals(current, "--session-db", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                sessionDbPath = args[++index];
                continue;
            }

            if (string.Equals(current, "--message-db", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                messageDbPath = args[++index];
                continue;
            }

            if (string.Equals(current, "--wxid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                wxid = args[++index];
                continue;
            }

            if (string.Equals(current, "--key", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                key = args[++index];
                continue;
            }

            if (string.Equals(current, "--output-json", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                outputJsonPath = args[++index];
                continue;
            }

            if (string.Equals(current, "--output-sqlite", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                outputSqlitePath = args[++index];
            }
        }

        return new CliOptions(
            string.IsNullOrWhiteSpace(mode) ? "probe" : mode.Trim(),
            string.IsNullOrWhiteSpace(accountDir) ? string.Empty : accountDir.Trim(),
            string.IsNullOrWhiteSpace(sessionDbPath) ? string.Empty : sessionDbPath.Trim(),
            string.IsNullOrWhiteSpace(messageDbPath) ? string.Empty : messageDbPath.Trim(),
            string.IsNullOrWhiteSpace(wxid) ? string.Empty : wxid.Trim(),
            string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim(),
            string.IsNullOrWhiteSpace(outputJsonPath) ? string.Empty : outputJsonPath.Trim(),
            string.IsNullOrWhiteSpace(outputSqlitePath) ? string.Empty : outputSqlitePath.Trim()
        );
    }
}

internal sealed class AppResult
{
    public bool Success { get; init; }
    public string Mode { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; init; } = [];
    public ProbeResult? Probe { get; init; }
    public string? OutputJsonPath { get; init; }
    public string? OutputSqlitePath { get; init; }
}

internal sealed class ProbeResult
{
    public string AccountDir { get; init; } = string.Empty;
    public string Wxid { get; init; } = string.Empty;
    public bool KeyProvided { get; init; }
    public int KeyLength { get; init; }
    public string? SessionDbPath { get; init; }
    public string? MessageDbPath { get; init; }
    public List<string> CandidateMessageDbPaths { get; init; } = [];
    public FileProbeResult? SessionDb { get; init; }
    public FileProbeResult? MessageDb { get; init; }
    public string Assessment { get; init; } = string.Empty;
    public string NextStep { get; init; } = string.Empty;
}

internal sealed class FileProbeResult
{
    public string Path { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public bool HasSqliteHeader { get; init; }
    public int? PageSizeFromHeader { get; init; }
    public List<int> CommonPageSizeCandidates { get; init; } = [];
    public string HeaderHex64 { get; init; } = string.Empty;
    public string HeaderHex256 { get; init; } = string.Empty;
    public string HeaderAsciiPreview { get; init; } = string.Empty;
    public double HeadEntropy { get; init; }
    public List<PageLayoutProbe> LayoutProbes { get; init; } = [];
}

internal sealed class PageLayoutProbe
{
    public int PageSize { get; init; }
    public int SampledPageCount { get; init; }
    public long EstimatedTotalPages { get; init; }
    public List<PageSample> PageSamples { get; init; } = [];
    public bool RepeatingTailPrefix { get; init; }
    public double AverageTailNonZeroBytes { get; init; }
}

internal sealed class PageSample
{
    public int PageIndex { get; init; }
    public long Offset { get; init; }
    public string StartHex16 { get; init; } = string.Empty;
    public string EndHex16 { get; init; } = string.Empty;
    public double Entropy { get; init; }
    public int TailNonZeroBytes { get; init; }
}
