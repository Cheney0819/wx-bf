using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Footprint.Core;

public interface IChatImageProtocolProvider
{
    ChatImageProtocolEvidence GetEvidence(string dllSha256);
}

public sealed class UnverifiedChatImageProtocolProvider : IChatImageProtocolProvider
{
    public ChatImageProtocolEvidence GetEvidence(string dllSha256) => ChatImageProtocolEvidence.Unverified(dllSha256);
}

public sealed class FileChatImageProtocolProvider(string evidencePath) : IChatImageProtocolProvider
{
    public ChatImageProtocolEvidence GetEvidence(string dllSha256)
    {
        if (!File.Exists(evidencePath)) return ChatImageProtocolEvidence.Unverified(dllSha256);
        try
        {
            var fullPath = Path.GetFullPath(evidencePath);
            if (!string.Equals(Path.GetFileName(fullPath), "image-protocol-evidence.json", StringComparison.Ordinal) ||
                !string.Equals(new DirectoryInfo(Path.GetDirectoryName(fullPath)!).Name, "capture", StringComparison.OrdinalIgnoreCase))
                return ChatImageProtocolEvidence.Unverified(dllSha256);
            var evidence = System.Text.Json.JsonSerializer.Deserialize<ChatImageProtocolEvidence>(
                File.ReadAllText(fullPath), TargetProfile.JsonOptions);
            if (evidence?.IsVerifiedFor(dllSha256) != true || evidence.KeySha256.Length is not (0 or 64) ||
                evidence.IvSha256.Length is not (0 or 64) || evidence.KeyLength < 0 || evidence.IvLength < 0)
                return ChatImageProtocolEvidence.Unverified(dllSha256);
            if (evidence.Passthrough) return evidence;
            var captureDirectory = Path.GetDirectoryName(fullPath)!;
            var keyPath = ResolveProtectedPath(captureDirectory, evidence.KeyProtectedPath);
            var xorPath = ResolveProtectedPath(captureDirectory, evidence.XorProtectedPath);
            return keyPath is null || xorPath is null ? ChatImageProtocolEvidence.Unverified(dllSha256) : evidence with
            {
                ResolvedKeyProtectedPath = keyPath,
                ResolvedXorProtectedPath = xorPath
            };
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return ChatImageProtocolEvidence.Unverified(dllSha256);
        }
    }

    private static string? ResolveProtectedPath(string captureDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var keysDirectory = Path.GetFullPath(Path.Combine(captureDirectory, "keys"));
        var candidate = Path.GetFullPath(Path.Combine(captureDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(Path.GetDirectoryName(candidate), keysDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetExtension(candidate), ".dpapi", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(candidate) ? candidate : null;
    }
}

public interface IChatImageSecretStore
{
    (byte[] Key, byte[] Xor) Load(ChatImageProtocolEvidence protocol);
}

public sealed class DpapiChatImageSecretStore : IChatImageSecretStore
{
    public (byte[] Key, byte[] Xor) Load(ChatImageProtocolEvidence protocol)
    {
        if (protocol.ResolvedKeyProtectedPath is null || protocol.ResolvedXorProtectedPath is null)
            throw new InvalidOperationException("protected_key_missing");
        return (ProtectedKeyStore.UnprotectFromFile(protocol.ResolvedKeyProtectedPath),
            ProtectedKeyStore.UnprotectFromFile(protocol.ResolvedXorProtectedPath));
    }
}

public interface IChatImageDecryptor
{
    Task DecryptAsync(string sourcePath, string destinationPath, ChatImageProtocolEvidence protocol,
        string dllSha256, CancellationToken cancellationToken);
}

public sealed class ChatImageDecryptor(IChatImageSecretStore? secretStore = null) : IChatImageDecryptor
{
    private static readonly byte[] Magic = [0x07, 0x08, 0x56, 0x32, 0x08, 0x07];
    private readonly IChatImageSecretStore _secretStore = secretStore ?? new DpapiChatImageSecretStore();

    public async Task DecryptAsync(string sourcePath, string destinationPath, ChatImageProtocolEvidence protocol,
        string dllSha256, CancellationToken cancellationToken)
    {
        if (!protocol.IsVerifiedFor(dllSha256)) throw new InvalidOperationException("protocol_not_verified");
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (protocol.Passthrough)
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                return;
            }

            var (key, xor) = _secretStore.Load(protocol);
            try
            {
                ValidateSecrets(protocol, key, xor);
                var header = new byte[15];
                await ReadExactlyAsync(input, header, cancellationToken);
                if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[14] != 1)
                    throw new InvalidDataException("invalid_image_container_header");
                var firstPlainLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6, 4));
                var xorLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10, 4));
                if (firstPlainLength is 0 or > 1024 || xorLength > 1024 * 1024)
                    throw new InvalidDataException("invalid_image_container_lengths");
                var firstCipherLength = (firstPlainLength & ~15u) + 16u;
                if (input.Length < 15L + firstCipherLength + xorLength)
                    throw new InvalidDataException("truncated_image_container");

                var firstCipher = new byte[checked((int)firstCipherLength)];
                await ReadExactlyAsync(input, firstCipher, cancellationToken);
                byte[] firstPlain;
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.PKCS7;
                    firstPlain = aes.DecryptEcb(firstCipher, PaddingMode.PKCS7);
                }
                if (firstPlain.Length != firstPlainLength)
                    throw new CryptographicException("image_first_segment_length_mismatch");
                await output.WriteAsync(firstPlain, cancellationToken);

                var remainingXor = (long)xorLength;
                var buffer = new byte[1024 * 1024];
                while (remainingXor > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remainingXor);
                    var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                    if (read == 0) throw new InvalidDataException("truncated_image_xor_segment");
                    for (var index = 0; index < read; index++) buffer[index] ^= xor[0];
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remainingXor -= read;
                }
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                CryptographicOperations.ZeroMemory(firstPlain);
                CryptographicOperations.ZeroMemory(firstCipher);
                CryptographicOperations.ZeroMemory(buffer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(xor);
            }
        }
        catch
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            throw;
        }
    }

    private static void ValidateSecrets(ChatImageProtocolEvidence protocol, byte[] key, byte[] xor)
    {
        if (key.Length != 16 || xor.Length != 1 ||
            !string.Equals(Hashing.Sha256Hex(key), protocol.KeySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Hashing.Sha256Hex(xor), protocol.XorSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("image_secret_fingerprint_mismatch");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new InvalidDataException("truncated_image_container");
            offset += read;
        }
    }
}
