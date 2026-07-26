using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Tests;

public sealed class EncryptedOutboxProtectorTests
{
    [Fact]
    public void ProtectedOutboxNeverContainsPlaintextPayload()
    {
        var protector = new EncryptedOutboxProtector(new XorTestProtector());

        var ciphertext = protector.Protect(
            "outbox-1",
            "messages",
            "secret-message"u8);

        Assert.Equal(-1, ciphertext.AsSpan().IndexOf("secret-message"u8));
        Assert.Equal(
            "secret-message"u8.ToArray(),
            protector.Unprotect("outbox-1", "messages", ciphertext));
    }

    [Fact]
    public void ContextMismatchCannotRecoverOriginalPayload()
    {
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        var ciphertext = protector.Protect("outbox-1", "messages", "payload"u8);

        var recovered = protector.Unprotect("outbox-2", "messages", ciphertext);

        Assert.NotEqual("payload"u8.ToArray(), recovered);
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext, entropy);

        private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy)
        {
            var output = input.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= entropy[index % entropy.Length];
            return output;
        }
    }
}
