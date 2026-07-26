namespace DesktopPet.DataSync.Security;

public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy);
}

public interface IOutboxProtector
{
    byte[] Protect(string outboxId, string endpoint, ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(string outboxId, string endpoint, ReadOnlySpan<byte> ciphertext);
}
