using System.Security.Cryptography;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Upload;

namespace DesktopPet.DataSync.Tests;

public sealed class ServerSettingsVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-server-settings-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingSettingsRemainOffline()
    {
        var vault = new ServerSettingsVault(
            Path.Combine(_root, "server-settings.dpapi"),
            new AuthenticatedTestProtector());

        Assert.Null(await vault.TryLoadAsync(default));
    }

    [Fact]
    public async Task ProtectedFileContainsNeitherUrlNorToken()
    {
        var path = Path.Combine(_root, "server-settings.dpapi");
        var vault = new ServerSettingsVault(path, new AuthenticatedTestProtector());
        var settings = new ServerSettings(
            new Uri("https://example.invalid/"),
            "top-secret-token");

        await vault.SaveAsync(settings, default);
        var bytes = await File.ReadAllBytesAsync(path);
        var loaded = await vault.TryLoadAsync(default);

        Assert.Equal(settings, loaded);
        Assert.Equal(-1, bytes.AsSpan().IndexOf("top-secret-token"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("example.invalid"u8));
    }

    [Fact]
    public async Task TamperedSettingsAreRejectedWithoutSecretInError()
    {
        var path = Path.Combine(_root, "server-settings.dpapi");
        var vault = new ServerSettingsVault(path, new AuthenticatedTestProtector());
        await vault.SaveAsync(
            new ServerSettings(new Uri("https://example.invalid/"), "top-secret-token"),
            default);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(path, bytes);

        var exception = await Assert.ThrowsAsync<CryptographicException>(() =>
            vault.TryLoadAsync(default));

        Assert.DoesNotContain("top-secret-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.invalid/")]
    [InlineData("https://user:pass@example.invalid/")]
    [InlineData("https://example.invalid/path")]
    public async Task UnsafeServerBaseUriIsRejected(string uri)
    {
        var vault = new ServerSettingsVault(
            Path.Combine(_root, "server-settings.dpapi"),
            new AuthenticatedTestProtector());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            vault.SaveAsync(new ServerSettings(new Uri(uri), "token"), default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class AuthenticatedTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var ciphertext = new byte[32 + plaintext.Length];
            var digest = HMACSHA256.HashData(entropy, plaintext);
            digest.CopyTo(ciphertext, 0);
            for (var index = 0; index < plaintext.Length; index++)
                ciphertext[32 + index] = (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            return ciphertext;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            if (ciphertext.Length < 33) throw new CryptographicException("tampered");
            var plaintext = new byte[ciphertext.Length - 32];
            for (var index = 0; index < plaintext.Length; index++)
                plaintext[index] = (byte)(ciphertext[32 + index] ^ entropy[index % entropy.Length]);
            var digest = HMACSHA256.HashData(entropy, plaintext);
            if (!CryptographicOperations.FixedTimeEquals(digest, ciphertext[..32]))
                throw new CryptographicException("tampered");
            return plaintext;
        }
    }
}
