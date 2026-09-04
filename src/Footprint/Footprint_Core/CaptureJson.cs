using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core;

public sealed record CaptureEnvelope(CaptureEvent Event, string? ProtectedKeyPath);

public static class CaptureJson
{
    public const string Schema = "Footprint_CaptureEvent_v1";
    private const string InvalidMessageZh = "采集事件格式无效。";
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "schema", "profile_sha256", "kind", "boundary", "thread_id", "wrapper", "core", "path", "tag",
        "page_size", "compatibility", "db_pointer", "key_sha256", "key_length", "timestamp_ms",
        "stack_fingerprint", "business_key_sha256", "path_from_db", "protected_key_path",
        "path_kind", "file_name", "path_fingerprint", "open_succeeded", "secret_source", "selector",
        "codec_pointer", "for_ctx", "source"
    };

    public static CaptureEnvelope Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any(property => !AllowedProperties.Contains(property.Name)))
                throw Invalid();

            var wire = JsonSerializer.Deserialize<CaptureWire>(line, TargetProfile.JsonOptions) ?? throw Invalid();
            if (!string.Equals(wire.Schema, Schema, StringComparison.Ordinal) || !IsSha256(wire.ProfileSha256) ||
                string.IsNullOrWhiteSpace(wire.Kind) || string.IsNullOrWhiteSpace(wire.Boundary) ||
                wire.TimestampMilliseconds <= 0)
                throw Invalid();

            if (string.Equals(wire.Kind, "profile", StringComparison.Ordinal) &&
                (wire.ThreadId <= 0 || string.IsNullOrWhiteSpace(wire.Wrapper) ||
                 string.IsNullOrWhiteSpace(wire.Core) || string.IsNullOrWhiteSpace(wire.Path) || wire.Tag is null))
                throw Invalid();
            if (string.Equals(wire.Kind, "key", StringComparison.Ordinal) &&
                (wire.ThreadId <= 0 || string.IsNullOrWhiteSpace(wire.DbPointer) ||
                 !IsSha256(wire.KeySha256) || wire.KeyLength <= 0))
                throw Invalid();

            var item = new CaptureEvent(wire.Kind, wire.Boundary, wire.ThreadId,
                wire.Wrapper, wire.Core, wire.Path, wire.Tag, wire.PageSize, wire.Compatibility, wire.DbPointer,
                wire.KeySha256, wire.KeyLength, wire.TimestampMilliseconds, wire.StackFingerprint,
                wire.BusinessKeySha256, wire.PathFromDb, wire.ProtectedKeyPath, wire.ProfileSha256);
            return new CaptureEnvelope(item, wire.ProtectedKeyPath);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw Invalid();
        }
    }

    public static string SerializeSanitized(CaptureEnvelope envelope) => JsonSerializer.Serialize(new
    {
        schema = Schema,
        profile_sha256 = envelope.Event.ProfileSha256,
        kind = envelope.Event.Kind,
        boundary = envelope.Event.Boundary,
        thread_id = envelope.Event.ThreadId,
        wrapper = envelope.Event.Wrapper,
        core = envelope.Event.Core,
        path = envelope.Event.Path,
        tag = envelope.Event.Tag,
        page_size = envelope.Event.PageSize,
        compatibility = envelope.Event.Compatibility,
        db_pointer = envelope.Event.DbPointer,
        key_sha256 = envelope.Event.KeySha256,
        key_length = envelope.Event.KeyLength,
        timestamp_ms = envelope.Event.TimestampMilliseconds,
        stack_fingerprint = envelope.Event.StackFingerprint,
        business_key_sha256 = envelope.Event.BusinessKeySha256,
        path_from_db = envelope.Event.PathFromDb,
        protected_key_path = envelope.ProtectedKeyPath
    }, TargetProfile.JsonOptions);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static InvalidDataException Invalid() => new(InvalidMessageZh);

    private sealed class CaptureWire
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("profile_sha256")] public string? ProfileSha256 { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("boundary")] public string? Boundary { get; init; }
        [JsonPropertyName("thread_id")] public int ThreadId { get; init; }
        [JsonPropertyName("wrapper")] public string? Wrapper { get; init; }
        [JsonPropertyName("core")] public string? Core { get; init; }
        [JsonPropertyName("path")] public string? Path { get; init; }
        [JsonPropertyName("tag")] public int? Tag { get; init; }
        [JsonPropertyName("page_size")] public int? PageSize { get; init; }
        [JsonPropertyName("compatibility")] public int? Compatibility { get; init; }
        [JsonPropertyName("db_pointer")] public string? DbPointer { get; init; }
        [JsonPropertyName("key_sha256")] public string? KeySha256 { get; init; }
        [JsonPropertyName("key_length")] public int? KeyLength { get; init; }
        [JsonPropertyName("timestamp_ms")] public long TimestampMilliseconds { get; init; }
        [JsonPropertyName("stack_fingerprint")] public string? StackFingerprint { get; init; }
        [JsonPropertyName("business_key_sha256")] public string? BusinessKeySha256 { get; init; }
        [JsonPropertyName("path_from_db")] public string? PathFromDb { get; init; }
        [JsonPropertyName("protected_key_path")] public string? ProtectedKeyPath { get; init; }
    }
}
