using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core.Runtime;

public sealed record CaptureRuntimeResource
{
    [JsonPropertyName("resource_name")] public string ResourceName { get; init; } = string.Empty;
    [JsonPropertyName("file_name")] public string FileName { get; init; } = string.Empty;
    [JsonPropertyName("length")] public long Length { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
}

public sealed record CaptureRuntimeManifest
{
    public const string EmbeddedResourceName = "Footprint.CaptureRuntime.Resources.manifest.json";
    private const string CurrentSchema = "Footprint_CaptureRuntime_Manifest_v1";

    [JsonPropertyName("schema")] public string Schema { get; init; } = string.Empty;
    [JsonPropertyName("runtime_identifier")] public string RuntimeIdentifier { get; init; } = string.Empty;
    [JsonPropertyName("bundle_sha256")] public string BundleSha256 { get; init; } = string.Empty;
    [JsonPropertyName("resources")] public IReadOnlyList<CaptureRuntimeResource> Resources { get; init; } = [];

    public static CaptureRuntimeManifest LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
                ?? throw new CaptureRuntimeException("capture_runtime_manifest_missing", "采集运行时资源清单缺失。");
            var manifest = JsonSerializer.Deserialize<CaptureRuntimeManifest>(stream)
                ?? throw new CaptureRuntimeException("capture_runtime_manifest_invalid", "采集运行时资源清单无效。");
            manifest.Validate();
            return manifest;
        }
        catch (CaptureRuntimeException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new CaptureRuntimeException("capture_runtime_manifest_invalid", "采集运行时资源清单无效。", error);
        }
    }

    public void Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal) ||
            !string.Equals(RuntimeIdentifier, "win-x64", StringComparison.Ordinal) ||
            Resources is null || Resources.Count == 0 ||
            Resources.Any(item => item is null) ||
            Resources.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Resources.Count ||
            Resources.Select(item => item.ResourceName).Distinct(StringComparer.Ordinal).Count() != Resources.Count)
            throw new CaptureRuntimeException("capture_runtime_manifest_invalid", "采集运行时资源清单无效。");

        foreach (var resource in Resources)
        {
            if (resource is null || !IsSafeRelativePath(resource.FileName))
                throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
            if (string.IsNullOrWhiteSpace(resource.ResourceName) ||
                !resource.ResourceName.StartsWith("Footprint.CaptureRuntime.Resources.", StringComparison.Ordinal) ||
                resource.Length < 0 || !IsLowerSha256(resource.Sha256))
                throw new CaptureRuntimeException("capture_runtime_manifest_invalid", "采集运行时资源清单无效。");
        }

        if (!IsLowerSha256(BundleSha256) ||
            !string.Equals(BundleSha256, ComputeBundleSha256(Resources), StringComparison.Ordinal))
            throw new CaptureRuntimeException("capture_runtime_manifest_invalid", "采集运行时资源清单无效。");
    }

    public static string ComputeBundleSha256(IEnumerable<CaptureRuntimeResource> resources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var resource in resources)
        {
            Append(resource.ResourceName);
            hash.AppendData([0]);
            Append(resource.FileName);
            hash.AppendData([0]);
            Append(resource.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hash.AppendData([0]);
            Append(resource.Sha256);
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    internal static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Any(character => character == '\0' || character < ' ' || character == ':')) return false;
        var normalized = value.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.None)
            .All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsLowerSha256(string value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class CaptureRuntimeException : IOException
{
    public CaptureRuntimeException(string code, string messageZh, Exception? innerException = null)
        : base(messageZh, innerException)
    {
        Code = code;
        MessageZh = messageZh;
    }

    public string Code { get; }
    public string MessageZh { get; }
}
