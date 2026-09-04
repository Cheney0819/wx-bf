using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Footprint.Core;

public sealed class TargetProfile
{
    public static readonly string[] RequiredRvas =
    [
        "wcdb_database_set_cipher_config", "wcdb_cpp_apply_sqlcipher_key",
        "sqlite3_key", "sqlite3_key_v2", "sqlite3CodecAttach", "sqlcipher_codec_ctx_init", "sqlite3_db_filename",
        "sqlcipher_codec_ctx_set_pass", "sqlite3_codec_set_pass", "sqlite3CodecGetKey",
        "wcdb_decompress", "sqlite3_prepare_v2", "sqlite3_prepare_v3", "sqlite3_step",
        "sqlite3_finalize", "sqlite3_column_count", "sqlite3_column_name", "sqlite3_column_type",
        "sqlite3_column_bytes", "sqlite3_column_blob", "sqlite3_column_text", "sqlite3_column_int64", "sqlite3_column_double", "sqlite3_errmsg",
        "sqlite3_threadsafe", "sqlite3_db_mutex", "sqlite3_mutex_enter", "sqlite3_mutex_leave",
        "wcdb_key_container_data", "wcdb_key_container_size",
        "image_container_decrypt_entry", "image_selector_key", "image_xor_key",
        "image_aes_decrypt", "image_xor_transform"
    ];
    private static readonly HashSet<string> AllowedRvas = new(RequiredRvas, StringComparer.Ordinal)
    {
        "wcdb_database_get_path_object"
    };

    private static readonly HashSet<string> TopLevelProperties = new(StringComparer.Ordinal)
    {
        "profile_id", "dll_sha256", "module_name", "rvas", "layout"
    };
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.Ordinal)
    {
        "wrapper_to_core", "core_to_path_object", "path_object_to_utf8", "core_to_tag",
        "config_page_size", "config_compatibility", "wcdb_handle_to_db"
    };
    private static readonly HashSet<string> EntryProperties = new(StringComparer.Ordinal)
    {
        "rva", "signature", "mask", "kind", "evidence"
    };
    private static readonly Regex ProfileIdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    [JsonPropertyName("profile_id")]
    public string ProfileId { get; init; } = string.Empty;

    [JsonPropertyName("dll_sha256")]
    public string DllSha256 { get; init; } = string.Empty;

    [JsonPropertyName("module_name")]
    public string ModuleName { get; init; } = string.Empty;

    [JsonPropertyName("rvas")]
    public Dictionary<string, ProfileEntry> Rvas { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("layout")]
    public ProfileLayout Layout { get; init; } = new();

    public static TargetProfile Load(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            ValidateSchema(document.RootElement);
            return JsonSerializer.Deserialize<TargetProfile>(bytes, JsonOptions)
                ?? throw new InvalidDataException("profile_schema_invalid");
        }
        catch (ProfileFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
        {
            throw new ProfileFormatException();
        }
    }

    public ProfileValidation Validate()
    {
        var errors = new List<string>();
        if (!ProfileIdPattern.IsMatch(ProfileId ?? string.Empty)) errors.Add("profile_id_invalid");
        if (DllSha256.Length != 64 || DllSha256.Any(c => !Uri.IsHexDigit(c))) errors.Add("dll_sha256_invalid");
        if (!string.Equals(ModuleName, "Weixin.dll", StringComparison.Ordinal)) errors.Add("module_name_invalid");

        if (Rvas is null)
        {
            errors.Add("rvas_required");
        }
        else foreach (var name in RequiredRvas)
            {
                if (!Rvas.TryGetValue(name, out var entry) || entry is null)
                {
                    errors.Add($"rva_missing:{name}");
                    continue;
                }

                if (entry.Rva == 0) errors.Add($"rva_zero:{name}");
                if (entry.Signature is null || entry.Signature.Length < 24) errors.Add($"signature_too_short:{name}");
                if (entry.Mask is null || entry.Signature is null || entry.Mask.Length != entry.Signature.Length)
                    errors.Add($"mask_length_mismatch:{name}");
                if (entry.Mask is { Length: > 0 } && entry.Mask.All(value => value == 0)) errors.Add($"mask_all_wildcards:{name}");
            }

        if (Layout is null || Layout.WrapperToCore != 0x68 || Layout.CoreToPathObject != 0x40 || Layout.CoreToTag != 0x530)
            errors.Add("layout_invalid");

        return new ProfileValidation(errors.Count == 0, errors);
    }

    private static void ValidateSchema(JsonElement root)
    {
        RequireObjectProperties(root, TopLevelProperties);
        RequireString(root.GetProperty("profile_id"));
        RequireString(root.GetProperty("dll_sha256"));
        RequireString(root.GetProperty("module_name"));

        var layout = root.GetProperty("layout");
        RequireObjectProperties(layout, LayoutProperties);
        foreach (var property in LayoutProperties) RequireInt32(layout.GetProperty(property));

        var rvas = root.GetProperty("rvas");
        if (rvas.ValueKind != JsonValueKind.Object) throw new InvalidDataException("profile_schema_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rva in rvas.EnumerateObject())
        {
            if (!AllowedRvas.Contains(rva.Name) || !names.Add(rva.Name))
                throw new InvalidDataException("profile_schema_invalid");
            ValidateEntry(rva.Value);
        }
        if (!RequiredRvas.All(names.Contains)) throw new InvalidDataException("profile_schema_invalid");
    }

    private static void ValidateEntry(JsonElement entry)
    {
        RequireObjectProperties(entry, EntryProperties);
        if (!entry.GetProperty("rva").TryGetUInt64(out _)) throw new InvalidDataException("profile_schema_invalid");
        RequireBase64(entry.GetProperty("signature"));
        RequireBase64(entry.GetProperty("mask"));
        RequireString(entry.GetProperty("evidence"));
        var kind = RequireString(entry.GetProperty("kind"));
        if (kind is not ("function" or "layoutAccessor")) throw new InvalidDataException("profile_schema_invalid");
    }

    private static void RequireObjectProperties(JsonElement element, ISet<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("profile_schema_invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name) || !names.Add(property.Name))
                throw new InvalidDataException("profile_schema_invalid");
        }
        if (!required.SetEquals(names)) throw new InvalidDataException("profile_schema_invalid");
    }

    private static void RequireInt32(JsonElement element)
    {
        if (!element.TryGetInt32(out _)) throw new InvalidDataException("profile_schema_invalid");
    }

    private static string RequireString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
            throw new InvalidDataException("profile_schema_invalid");
        return value;
    }

    private static void RequireBase64(JsonElement element)
    {
        try { _ = Convert.FromBase64String(RequireString(element)); }
        catch (FormatException) { throw new InvalidDataException("profile_schema_invalid"); }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public sealed class ProfileFormatException : Exception
{
    public const string InvalidProfileCode = "profile_schema_invalid";
    public const string InvalidProfileMessageZh = "微信版本配置无效，已停止采集且不会控制微信进程。";

    public ProfileFormatException() : base(InvalidProfileMessageZh)
    {
        Data[nameof(ErrorCode)] = InvalidProfileCode;
    }

    public string ErrorCode => InvalidProfileCode;
    public string MessageZh => InvalidProfileMessageZh;
}

public sealed class ProfileEntry
{
    [JsonPropertyName("rva")]
    public ulong Rva { get; init; }

    [JsonPropertyName("signature")]
    public byte[] Signature { get; init; } = [];

    [JsonPropertyName("mask")]
    public byte[] Mask { get; init; } = [];

    [JsonPropertyName("kind")]
    public ProfileEntryKind Kind { get; init; } = ProfileEntryKind.Function;

    [JsonPropertyName("evidence")]
    public string Evidence { get; init; } = string.Empty;
}

public enum ProfileEntryKind { Function, LayoutAccessor }

public sealed class ProfileLayout
{
    [JsonPropertyName("wrapper_to_core")]
    public int WrapperToCore { get; init; } = 0x68;
    [JsonPropertyName("core_to_path_object")]
    public int CoreToPathObject { get; init; } = 0x40;
    [JsonPropertyName("path_object_to_utf8")]
    public int PathObjectToUtf8 { get; init; }
    [JsonPropertyName("core_to_tag")]
    public int CoreToTag { get; init; } = 0x530;
    [JsonPropertyName("config_page_size")]
    public int ConfigPageSize { get; init; } = 0x220;
    [JsonPropertyName("config_compatibility")]
    public int ConfigCompatibility { get; init; } = 0x224;
    [JsonPropertyName("wcdb_handle_to_db")]
    public int WcdbHandleToDb { get; init; } = 0x38;
}

public sealed record ProfileValidation(bool IsValid, IReadOnlyList<string> Errors);
