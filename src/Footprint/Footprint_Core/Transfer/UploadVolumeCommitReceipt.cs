using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Footprint.Core.Transfer;

public sealed record UploadVolumeCommitReceipt
{
    public UploadVolumeCommitReceipt(string runId, int volumeNumber, int totalVolumes, long packageLength,
        string packageSha256, DateTimeOffset committedAtUtc)
    {
        RunId = RunPackageContract.CanonicalRunId(runId);
        RunPackageContract.ValidateVolumeIdentity(volumeNumber, totalVolumes);
        VolumeNumber = volumeNumber;
        TotalVolumes = totalVolumes;
        if (packageLength < 0) throw new ArgumentOutOfRangeException(nameof(packageLength));
        PackageLength = packageLength;
        PackageSha256 = RunPackageContract.ValidateSha256(packageSha256, nameof(packageSha256));
        if (committedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Receipt commit timestamp must be UTC.", nameof(committedAtUtc));
        CommittedAtUtc = committedAtUtc;
    }

    public string RunId { get; }
    public int VolumeNumber { get; }
    public int TotalVolumes { get; }
    public long PackageLength { get; }
    public string PackageSha256 { get; }
    public DateTimeOffset CommittedAtUtc { get; }
}

public sealed class VerifiedUploadVolumeCommitReceipt
{
    private readonly byte[] _signature;
    internal VerifiedUploadVolumeCommitReceipt(UploadVolumeCommitReceipt receipt, byte[] signature)
    {
        Receipt = receipt;
        _signature = signature.ToArray();
    }
    public UploadVolumeCommitReceipt Receipt { get; }
    public byte[] Signature => _signature.ToArray();
}

public static class UploadVolumeCommitReceiptCodec
{
    private static readonly string[] ExactProperties =
        ["runId", "volumeNumber", "totalVolumes", "packageLength", "packageSha256", "committedAtUtc"];

    public static byte[] Serialize(UploadVolumeCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream(320);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("runId", receipt.RunId);
            writer.WriteNumber("volumeNumber", receipt.VolumeNumber);
            writer.WriteNumber("totalVolumes", receipt.TotalVolumes);
            writer.WriteNumber("packageLength", receipt.PackageLength);
            writer.WriteString("packageSha256", receipt.PackageSha256);
            writer.WriteString("committedAtUtc",
                receipt.CommittedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static UploadVolumeCommitReceipt Deserialize(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject().Select(property => property.Name).SequenceEqual(ExactProperties,
                    StringComparer.Ordinal))
                throw new InvalidDataException("Volume receipt fields or field order are invalid.");
            var committedText = root.GetProperty("committedAtUtc").GetString() ??
                                throw new InvalidDataException("Volume receipt timestamp is missing.");
            if (!DateTimeOffset.TryParseExact(committedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var committed))
                throw new InvalidDataException("Volume receipt timestamp is not canonical UTC.");
            return new UploadVolumeCommitReceipt(
                root.GetProperty("runId").GetString() ?? throw new InvalidDataException("Volume receipt RunId is missing."),
                root.GetProperty("volumeNumber").GetInt32(),
                root.GetProperty("totalVolumes").GetInt32(),
                root.GetProperty("packageLength").GetInt64(),
                root.GetProperty("packageSha256").GetString() ?? throw new InvalidDataException("Volume receipt hash is missing."),
                committed);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Volume receipt JSON is invalid.", exception);
        }
    }

    public static byte[] Sign(UploadVolumeCommitReceipt receipt, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        UploadCommitReceiptCodec.ValidateP256(privateKey, nameof(privateKey));
        return privateKey.SignData(Serialize(receipt), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

public static class UploadVolumeCommitReceiptValidator
{
    public static VerifiedUploadVolumeCommitReceipt Verify(UploadVolumeCommitReceipt receipt,
        ReadOnlySpan<byte> signature, ECDsa serverReceiptKey, RunPackageVolumeArtifact expected)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(serverReceiptKey);
        ArgumentNullException.ThrowIfNull(expected);
        UploadCommitReceiptCodec.ValidateP256(serverReceiptKey, nameof(serverReceiptKey));
        if (signature.Length != 64 || !serverReceiptKey.VerifyData(UploadVolumeCommitReceiptCodec.Serialize(receipt),
                signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Volume receipt signature verification failed.");
        if (!string.Equals(receipt.RunId, expected.RunId, StringComparison.Ordinal) ||
            receipt.VolumeNumber != expected.VolumeNumber || receipt.TotalVolumes != expected.TotalVolumes ||
            receipt.PackageLength != expected.PackageLength ||
            !string.Equals(receipt.PackageSha256, expected.PackageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Volume receipt identity does not match the ZIP64 volume.");
        return new VerifiedUploadVolumeCommitReceipt(receipt, signature.ToArray());
    }
}
