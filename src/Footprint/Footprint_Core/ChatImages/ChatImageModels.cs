using System.Text.Json.Serialization;

namespace Footprint.Core;

public enum ChatImageVariant
{
    Full,
    Thumbnail,
    Unknown
}

public enum ChatImageStatus
{
    Passed,
    Failed,
    Missing
}

public sealed record ChatImageIndexRecord
{
    public long RowId { get; init; }
    public string? Md5Hash { get; init; }
    public string? Md5 { get; init; }
    public int? Type { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long? FileSize { get; init; }
    public string? ModifyTime { get; init; }
    public int? Dir1Id { get; init; }
    public int? Dir2Id { get; init; }
    public string? Dir1Name { get; init; }
    public string? Dir2Name { get; init; }
    public string? ExtraBufferHex { get; init; }
    public ChatImageVariant Variant { get; init; }
    public string? ResourceStem { get; init; }
    public long? ResourceId { get; init; }
    public long? ResourceMessageId { get; init; }
    public int? ResourceType { get; init; }
    public long? ResourceSize { get; init; }
    public int? ResourceStatus { get; init; }
    public IReadOnlyList<ChatImageResourceRecord> ResourceCandidates { get; init; } = [];
}

public sealed record ChatImageIndexReadResult(
    string DatabasePath,
    IReadOnlyList<ChatImageIndexRecord> Items,
    IReadOnlyList<string> Errors)
{
    public int Expected => Items.Count;
}

public sealed record ChatImageLocation(
    ChatImageIndexRecord Index,
    IReadOnlyList<string> Candidates,
    string? ErrorCode)
{
    public bool IsLocated => Candidates.Count == 1 && ErrorCode is null;
}

public sealed record ChatImageResourceRecord
{
    public long ResourceId { get; init; }
    public long? MessageId { get; init; }
    public int? Type { get; init; }
    public long? Size { get; init; }
    public long? CreateTime { get; init; }
    public long? AccessTime { get; init; }
    public int? Status { get; init; }
    public string? DataIndex { get; init; }
    public string? PackedInfoHex { get; init; }
    public string? Stem { get; init; }
}

public sealed record ChatImageResourceReadResult(
    string DatabasePath,
    IReadOnlyList<ChatImageResourceRecord> Items,
    IReadOnlyList<string> Errors)
{
    public int Expected => Items.Count + Errors.Count;
}

public sealed record ChatImageResourceMatch(
    ChatImageResourceRecord Resource,
    IReadOnlyList<string> Candidates);

public sealed class ChatImageMediaSnapshotSummary
{
    [JsonPropertyName("directory")] public string? Directory { get; set; }
    [JsonPropertyName("copied_file_count")] public int CopiedFileCount { get; set; }
    [JsonPropertyName("matched_index_count")] public int MatchedIndexCount { get; set; }
    [JsonPropertyName("missing_index_count")] public int MissingIndexCount { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
}

public sealed record ChatImageProtocolEvidence
{
    public string DllSha256 { get; init; } = string.Empty;
    public string ProtocolId { get; init; } = string.Empty;
    public string Algorithm { get; init; } = string.Empty;
    public string KeySha256 { get; init; } = string.Empty;
    public int KeyLength { get; init; }
    public string IvSha256 { get; init; } = string.Empty;
    public int IvLength { get; init; }
    public string XorSha256 { get; init; } = string.Empty;
    public int XorLength { get; init; }
    public string ContainerMagic { get; init; } = string.Empty;
    public int KeySelector { get; init; }
    public string? KeyProtectedPath { get; init; }
    public string? XorProtectedPath { get; init; }
    [JsonIgnore] public string? ResolvedKeyProtectedPath { get; init; }
    [JsonIgnore] public string? ResolvedXorProtectedPath { get; init; }
    public int MatchingInputCount { get; init; }
    public int MatchingOutputCount { get; init; }
    public bool Verified { get; init; }
    public bool Passthrough { get; init; }

    public bool IsVerifiedFor(string dllSha256)
    {
        var common = Verified && !string.IsNullOrWhiteSpace(dllSha256) &&
        string.Equals(DllSha256, dllSha256, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ProtocolId) && !string.IsNullOrWhiteSpace(Algorithm) &&
        MatchingInputCount >= 2 && MatchingOutputCount >= 2;
        if (!common) return false;
        if (Passthrough) return string.Equals(Algorithm, "passthrough", StringComparison.Ordinal);
        return string.Equals(ProtocolId, "weixin-v2-aes-128-ecb-xor", StringComparison.Ordinal) &&
               string.Equals(Algorithm, "AES-128-ECB-PKCS7+XOR", StringComparison.Ordinal) &&
               string.Equals(ContainerMagic, "070856320807", StringComparison.OrdinalIgnoreCase) &&
               KeySelector == 2 && KeyLength == 16 && XorLength == 1 && IvLength == 0 &&
               KeySha256.Length == 64 && XorSha256.Length == 64 &&
               !string.IsNullOrWhiteSpace(KeyProtectedPath) && !string.IsNullOrWhiteSpace(XorProtectedPath);
    }

    public static ChatImageProtocolEvidence Unverified(string dllSha256) => new() { DllSha256 = dllSha256 };

    public static ChatImageProtocolEvidence VerifiedPassthrough(string dllSha256) => new()
    {
        DllSha256 = dllSha256,
        ProtocolId = "test-passthrough",
        Algorithm = "passthrough",
        MatchingInputCount = 2,
        MatchingOutputCount = 2,
        Verified = true,
        Passthrough = true
    };
}

public sealed record ChatImageVerification(
    string Header,
    string Decoder,
    string HardlinkMd5,
    string HardlinkSize,
    string Format,
    int Width,
    int Height,
    long OutputSize,
    string OutputMd5,
    string OutputSha256,
    string? ErrorCode,
    string? ErrorSummary)
{
    public bool Passed => ErrorCode is null && Header == "passed" && Decoder == "passed" && Width > 0 && Height > 0 &&
                          HardlinkMd5 is "passed" or "not_applicable" &&
                          HardlinkSize is "passed" or "not_applicable";
}

public sealed class ChatImageManifest
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("status")] public string Status { get; set; } = "not_run";
    [JsonPropertyName("account_root")] public string? AccountRoot { get; set; }
    [JsonPropertyName("index_database")] public string? IndexDatabase { get; set; }
    [JsonPropertyName("expected")] public int Expected { get; set; }
    [JsonPropertyName("located")] public int Located { get; set; }
    [JsonPropertyName("passed")] public int Passed { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("missing")] public int Missing { get; set; }
    [JsonPropertyName("runtime_path_probe")] public ChatImageRuntimePathProbeSummary RuntimePathProbe { get; set; } = new();
    [JsonPropertyName("media_snapshot")] public ChatImageMediaSnapshotSummary MediaSnapshot { get; set; } = new();
    [JsonPropertyName("work_cleanup_status")] public string WorkCleanupStatus { get; set; } = "passed";
    [JsonPropertyName("work_cleanup_error_code")] public string? WorkCleanupErrorCode { get; set; }
    [JsonPropertyName("work_cleanup_error_summary")] public string? WorkCleanupErrorSummary { get; set; }
    [JsonPropertyName("items")] public List<ChatImageManifestItem> Items { get; init; } = [];
}

public sealed class ChatImageRuntimePathProbeSummary
{
    [JsonPropertyName("event_count")] public int EventCount { get; set; }
    [JsonPropertyName("media_event_count")] public int MediaEventCount { get; set; }
    [JsonPropertyName("matched_index_count")] public int MatchedIndexCount { get; set; }
    [JsonPropertyName("successful_open_count")] public int SuccessfulOpenCount { get; set; }
    [JsonPropertyName("root_counts")] public Dictionary<string, int> RootCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChatImageManifestItem
{
    [JsonPropertyName("index_rowid")] public long IndexRowId { get; init; }
    [JsonPropertyName("source_file_name")] public string SourceFileName { get; init; } = string.Empty;
    [JsonPropertyName("source_relative_path")] public string? SourceRelativePath { get; init; }
    [JsonPropertyName("source_size")] public long? SourceSize { get; init; }
    [JsonPropertyName("source_sha256")] public string? SourceSha256 { get; init; }
    [JsonPropertyName("hardlink_md5")] public string? HardlinkMd5 { get; init; }
    [JsonPropertyName("dir1_id")] public int? Dir1Id { get; init; }
    [JsonPropertyName("dir1_name")] public string? Dir1Name { get; init; }
    [JsonPropertyName("dir2_id")] public int? Dir2Id { get; init; }
    [JsonPropertyName("dir2_name")] public string? Dir2Name { get; init; }
    [JsonPropertyName("resource_id")] public long? ResourceId { get; init; }
    [JsonPropertyName("resource_message_id")] public long? ResourceMessageId { get; init; }
    [JsonPropertyName("resource_type")] public int? ResourceType { get; init; }
    [JsonPropertyName("resource_size")] public long? ResourceSize { get; init; }
    [JsonPropertyName("resource_status")] public int? ResourceStatus { get; init; }
    [JsonPropertyName("resource_stem")] public string? ResourceStem { get; init; }
    [JsonPropertyName("resource_candidates")] public List<ChatImageResourceManifestCandidate> ResourceCandidates { get; init; } = [];
    [JsonPropertyName("variant")] public string Variant { get; init; } = "unknown";
    [JsonPropertyName("status")] public string Status { get; set; } = "failed";
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("output_size")] public long? OutputSize { get; set; }
    [JsonPropertyName("output_md5")] public string? OutputMd5 { get; set; }
    [JsonPropertyName("output_sha256")] public string? OutputSha256 { get; set; }
    [JsonPropertyName("published_path")] public string? PublishedPath { get; set; }
    [JsonPropertyName("verification")] public ChatImageVerification? Verification { get; set; }
    [JsonPropertyName("candidate_diagnostics")] public List<ChatImageCandidateDiagnostic> CandidateDiagnostics { get; init; } = [];
    [JsonPropertyName("cleanup_status")] public string CleanupStatus { get; set; } = "passed";
    [JsonPropertyName("cleanup_source_size")] public long? CleanupSourceSize { get; set; }
    [JsonPropertyName("cleanup_source_sha256")] public string? CleanupSourceSha256 { get; set; }
    [JsonPropertyName("cleanup_verification")] public ChatImageVerification? CleanupVerification { get; set; }
    [JsonPropertyName("cleanup_error_code")] public string? CleanupErrorCode { get; set; }
    [JsonPropertyName("cleanup_error_summary")] public string? CleanupErrorSummary { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("error_summary")] public string? ErrorSummary { get; set; }
}

public sealed class ChatImageCandidateDiagnostic
{
    [JsonPropertyName("source_size")] public long? SourceSize { get; set; }
    [JsonPropertyName("source_sha256")] public string? SourceSha256 { get; set; }
    [JsonPropertyName("decrypted_size")] public long? DecryptedSize { get; set; }
    [JsonPropertyName("decrypted_sha256")] public string? DecryptedSha256 { get; set; }
    [JsonPropertyName("decrypted_prefix_hex")] public string? DecryptedPrefixHex { get; set; }
    [JsonPropertyName("input_format")] public string? InputFormat { get; set; }
    [JsonPropertyName("wxgf_width")] public int? WxgfWidth { get; set; }
    [JsonPropertyName("wxgf_height")] public int? WxgfHeight { get; set; }
    [JsonPropertyName("wxgf_hevc_offset")] public int? WxgfHevcOffset { get; set; }
    [JsonPropertyName("ffmpeg_exit_code")] public int? FfmpegExitCode { get; set; }
    [JsonPropertyName("ffmpeg_timed_out")] public bool? FfmpegTimedOut { get; set; }
    [JsonPropertyName("ffmpeg_output_size")] public long? FfmpegOutputSize { get; set; }
    [JsonPropertyName("ffmpeg_error_summary")] public string? FfmpegErrorSummary { get; set; }
    [JsonPropertyName("normalized_size")] public long? NormalizedSize { get; set; }
    [JsonPropertyName("normalized_sha256")] public string? NormalizedSha256 { get; set; }
    [JsonPropertyName("normalized_prefix_hex")] public string? NormalizedPrefixHex { get; set; }
    [JsonPropertyName("verification")] public ChatImageVerification? Verification { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("error_summary")] public string? ErrorSummary { get; set; }
}

public sealed class ChatImageResourceManifestCandidate
{
    [JsonPropertyName("resource_id")] public long ResourceId { get; init; }
    [JsonPropertyName("message_id")] public long? MessageId { get; init; }
    [JsonPropertyName("type")] public int? Type { get; init; }
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
}

public sealed record ChatImageRecoveryResult(ChatImageManifest Manifest, string ManifestPath, string FailuresPath);
