using System.Security.Cryptography;
using System.Text;

namespace DesktopPet.DataSync.Security;

public sealed class EncryptedOutboxProtector : IOutboxProtector
{
    private readonly ISecretProtector _protector;

    public EncryptedOutboxProtector(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
    }

    public byte[] Protect(
        string outboxId,
        string endpoint,
        ReadOnlySpan<byte> plaintext)
    {
        Validate(outboxId, endpoint, plaintext, nameof(plaintext));
        var entropy = CreateEntropy(outboxId, endpoint);
        try
        {
            return _protector.Protect(plaintext, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public byte[] Unprotect(
        string outboxId,
        string endpoint,
        ReadOnlySpan<byte> ciphertext)
    {
        Validate(outboxId, endpoint, ciphertext, nameof(ciphertext));
        var entropy = CreateEntropy(outboxId, endpoint);
        try
        {
            return _protector.Unprotect(ciphertext, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static byte[] CreateEntropy(string outboxId, string endpoint) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"desktop-pet-datasync-outbox-v1\0{outboxId}\0{endpoint}"));

    private static void Validate(
        string outboxId,
        string endpoint,
        ReadOnlySpan<byte> value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (value.IsEmpty)
            throw new ArgumentException("Encrypted Outbox data must not be empty.", parameterName);
    }
}
