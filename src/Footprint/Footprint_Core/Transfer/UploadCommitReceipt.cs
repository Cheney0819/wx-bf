using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Footprint.Core.Transfer;

public sealed record UploadCommitReceipt
{
    public UploadCommitReceipt(string runId, long packageLength, string packageSha256, DateTimeOffset committedAtUtc)
    {
        var validatedRunId = RunPackageContract.ValidateRunId(runId);
        var canonicalRunId = RunPackageContract.CanonicalRunId(validatedRunId);
        if (!string.Equals(validatedRunId, canonicalRunId, StringComparison.Ordinal))
            throw new ArgumentException("Receipt Run ID must already use the canonical transport form.", nameof(runId));
        RunId = canonicalRunId;
        if (packageLength < 0) throw new ArgumentOutOfRangeException(nameof(packageLength));
        PackageLength = packageLength;
        PackageSha256 = RunPackageContract.ValidateSha256(packageSha256, nameof(packageSha256));
        if (committedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Receipt commit timestamp must be UTC.", nameof(committedAtUtc));
        CommittedAtUtc = committedAtUtc;
    }

    public string RunId { get; }
    public long PackageLength { get; }
    public string PackageSha256 { get; }
    public DateTimeOffset CommittedAtUtc { get; }
}

public sealed class VerifiedUploadCommitReceipt
{
    private readonly byte[] _signature;

    internal VerifiedUploadCommitReceipt(UploadCommitReceipt receipt, byte[] signature)
    {
        Receipt = receipt;
        _signature = signature.ToArray();
    }

    public UploadCommitReceipt Receipt { get; }
    public byte[] Signature => _signature.ToArray();
}

public static class UploadCommitReceiptCodec
{
    private static readonly string[] ExactProperties =
        ["runId", "packageLength", "packageSha256", "committedAtUtc"];

    public static byte[] Serialize(UploadCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("runId", receipt.RunId);
            writer.WriteNumber("packageLength", receipt.PackageLength);
            writer.WriteString("packageSha256", receipt.PackageSha256);
            writer.WriteString("committedAtUtc",
                receipt.CommittedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static UploadCommitReceipt Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Receipt must be a JSON object.");
            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (!properties.SequenceEqual(ExactProperties, StringComparer.Ordinal))
                throw new InvalidDataException("Receipt fields or field order do not match the frozen contract.");

            var runId = root.GetProperty("runId").GetString() ?? throw new InvalidDataException("Receipt Run ID is missing.");
            var length = root.GetProperty("packageLength").GetInt64();
            var sha256 = root.GetProperty("packageSha256").GetString() ??
                         throw new InvalidDataException("Receipt package hash is missing.");
            var committedText = root.GetProperty("committedAtUtc").GetString() ??
                                throw new InvalidDataException("Receipt commit timestamp is missing.");
            if (!DateTimeOffset.TryParseExact(committedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var committedAtUtc))
                throw new InvalidDataException("Receipt commit timestamp is not canonical UTC.");
            return new UploadCommitReceipt(runId, length, sha256, committedAtUtc);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Receipt JSON is invalid.", exception);
        }
    }

    public static byte[] Sign(UploadCommitReceipt receipt, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ValidateP256(privateKey, nameof(privateKey));
        return privateKey.SignData(Serialize(receipt), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal static void ValidateP256(ECDsa key, string parameterName)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (key.KeySize != 256 || parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32 ||
            !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal))
            throw new ArgumentException("Receipt signatures require ECDSA P-256.", parameterName);
    }
}

public static class UploadCommitReceiptValidator
{
    public static VerifiedUploadCommitReceipt Verify(UploadCommitReceipt receipt, ReadOnlySpan<byte> signature,
        ECDsa serverReceiptKey, RunPackageArtifact expected)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(serverReceiptKey);
        ArgumentNullException.ThrowIfNull(expected);
        UploadCommitReceiptCodec.ValidateP256(serverReceiptKey, nameof(serverReceiptKey));
        if (signature.Length != 64 || !serverReceiptKey.VerifyData(UploadCommitReceiptCodec.Serialize(receipt), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Receipt signature verification failed.");
        if (!string.Equals(receipt.RunId, expected.RunId, StringComparison.Ordinal) ||
            receipt.PackageLength != expected.PackageLength ||
            !string.Equals(receipt.PackageSha256, expected.PackageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Receipt identity does not match the complete ZIP64 package.");
        return new VerifiedUploadCommitReceipt(receipt, signature.ToArray());
    }
}
