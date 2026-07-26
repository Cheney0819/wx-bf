namespace DesktopPet.Recovery.Security;

public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy);
}
