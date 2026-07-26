using System.Runtime.Versioning;
using Wx411.Core.Windows;

namespace DesktopPet.Recovery.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly WindowsDpapiProtector _protector = new();

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        _protector.Protect(plaintext, entropy);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
        _protector.Unprotect(ciphertext, entropy);
}
