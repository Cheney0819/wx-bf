using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wx411.Core;

public interface ICapturePayloadProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy);
}

public sealed class PendingCaptureRecord : IDisposable
{
    private byte[]? _capturedPayload;

    internal PendingCaptureRecord(
        string recordId,
        string databaseSaltFingerprint,
        string moduleSha256,
        string callpointName,
        DateTime capturedAtUtc,
        byte[] capturedPayload)
    {
        RecordId = recordId;
        DatabaseSaltFingerprint = databaseSaltFingerprint;
        ModuleSha256 = moduleSha256;
        CallpointName = callpointName;
        CapturedAtUtc = capturedAtUtc;
        _capturedPayload = capturedPayload;
    }

    public string RecordId { get; }

    public string DatabaseSaltFingerprint { get; }

    public string ModuleSha256 { get; }

    public string CallpointName { get; }

    public DateTime CapturedAtUtc { get; }

    public byte[] CapturedPayload
    {
        get
        {
            ObjectDisposedException.ThrowIf(_capturedPayload is null, this);
            return _capturedPayload;
        }
    }

    public void Dispose()
    {
        var payload = Interlocked.Exchange(ref _capturedPayload, null);
        if (payload is not null) CryptographicOperations.ZeroMemory(payload);
    }
}

public sealed class PendingCaptureVault
{
    private const int CurrentVersion = 1;
    private readonly string _root;
    private readonly ICapturePayloadProtector _protector;
    private readonly Func<string, IEnumerable<string>> _enumerateCaptureFiles;

    public PendingCaptureVault(string root, ICapturePayloadProtector protector)
        : this(
            root,
            protector,
            static path => Directory.EnumerateFiles(path, "*.capture", SearchOption.AllDirectories))
    {
    }

    internal PendingCaptureVault(
        string root,
        ICapturePayloadProtector protector,
        Func<string, IEnumerable<string>> enumerateCaptureFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(enumerateCaptureFiles);
        _root = Path.GetFullPath(root);
        _protector = protector;
        _enumerateCaptureFiles = enumerateCaptureFiles;
    }

    public string Save(
        string databaseSaltFingerprint,
        string moduleSha256,
        string callpointName,
        ReadOnlySpan<byte> capturedPayload)
    {
        ValidateMetadata(databaseSaltFingerprint, moduleSha256, callpointName);
        if (capturedPayload.IsEmpty)
            throw new ArgumentException("Captured payload must not be empty.", nameof(capturedPayload));

        var payloadHash = SHA256.HashData(capturedPayload);
        var recordIdBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{databaseSaltFingerprint}|{moduleSha256}|{callpointName}|{Convert.ToHexString(payloadHash)}"));
        var recordId = Convert.ToHexString(recordIdBytes).ToLowerInvariant();
        var entropy = MakeEntropy(databaseSaltFingerprint, moduleSha256);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = _protector.Protect(capturedPayload, entropy);
            var envelope = new VaultEnvelope(
                CurrentVersion,
                databaseSaltFingerprint,
                moduleSha256,
                callpointName,
                DateTime.UtcNow,
                Convert.ToBase64String(ciphertext));
            var directory = DirectoryFor(databaseSaltFingerprint);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, recordId + ".capture");
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(envelope), new UTF8Encoding(false));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }

            return recordId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
            CryptographicOperations.ZeroMemory(recordIdBytes);
            CryptographicOperations.ZeroMemory(entropy);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public IReadOnlyList<PendingCaptureRecord> LoadMatching(
        string databaseSaltFingerprint,
        string moduleSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseSaltFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSha256);
        var directory = DirectoryFor(databaseSaltFingerprint);
        if (!Directory.Exists(directory)) return [];

        var records = new List<PendingCaptureRecord>();
        var completed = false;
        try
        {
            foreach (var path in Directory
                         .EnumerateFiles(directory, "*.capture", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                byte[]? ciphertext = null;
                byte[]? entropy = null;
                byte[]? payload = null;
                try
                {
                    var envelope = JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllText(path));
                    if (envelope is null || envelope.Version != CurrentVersion ||
                        !string.Equals(envelope.DatabaseSaltFingerprint, databaseSaltFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(envelope.ModuleSha256, moduleSha256, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(envelope.CallpointName))
                        continue;
                    if (string.IsNullOrWhiteSpace(envelope.Ciphertext))
                        throw new FormatException("Vault ciphertext is missing.");

                    ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                    entropy = MakeEntropy(databaseSaltFingerprint, moduleSha256);
                    payload = _protector.Unprotect(ciphertext, entropy);
                    if (payload.Length == 0) throw new CryptographicException("Vault payload is empty.");

                    var recordId = Path.GetFileNameWithoutExtension(path);
                    records.Add(new PendingCaptureRecord(
                        recordId,
                        databaseSaltFingerprint,
                        moduleSha256,
                        envelope.CallpointName,
                        envelope.CapturedAtUtc,
                        payload));
                    payload = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           JsonException or FormatException or ArgumentException or
                                           CryptographicException)
                {
                    TryDelete(path);
                }
                finally
                {
                    if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                    if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
                    if (payload is not null) CryptographicOperations.ZeroMemory(payload);
                }
            }

            completed = true;
            return Array.AsReadOnly(records.ToArray());
        }
        finally
        {
            if (!completed)
                foreach (var record in records) record.Dispose();
        }
    }

    public IReadOnlyList<string> SnapshotRecordIds()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        var ids = _enumerateCaptureFiles(_root)
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(id => id is { Length: 64 } && id.All(Uri.IsHexDigit))
            .Select(id => id.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(ids);
    }

    public void Delete(string databaseSaltFingerprint, string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseSaltFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        if (recordId.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Vault record identifier must be hexadecimal.", nameof(recordId));
        TryDelete(Path.Combine(DirectoryFor(databaseSaltFingerprint), recordId + ".capture"));
    }

    private string DirectoryFor(string databaseSaltFingerprint)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(databaseSaltFingerprint));
        try
        {
            return Path.Combine(_root, Convert.ToHexString(digest).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static byte[] MakeEntropy(string databaseSaltFingerprint, string moduleSha256) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"Wx411Easy.PendingCapture.v1|{databaseSaltFingerprint}|{moduleSha256}"));

    private static void ValidateMetadata(
        string databaseSaltFingerprint,
        string moduleSha256,
        string callpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseSaltFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(callpointName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record VaultEnvelope(
        int Version,
        string DatabaseSaltFingerprint,
        string ModuleSha256,
        string CallpointName,
        DateTime CapturedAtUtc,
        string Ciphertext);
}
