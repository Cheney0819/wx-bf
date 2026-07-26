using System.Security.Cryptography;
using DesktopPet.Recovery.Security;
using Wx411.Core;

namespace DesktopPet.Recovery.Tests;

public sealed class ValidatedKeyVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-key-vault-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void StoreNeverWritesPlaintextAndRoundTrips()
    {
        var vault = new ValidatedKeyVault(_root, new XorTestProtector());
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

        var id = vault.Store(Metadata("message_0.db"), key);
        var path = Assert.Single(Directory.EnumerateFiles(_root, "*.vkey"));
        var raw = File.ReadAllBytes(path);
        using var loaded = vault.Load(id);

        Assert.DoesNotContain(Convert.ToHexString(key), Convert.ToHexString(raw));
        Assert.Equal(key, loaded.Key);
        Assert.Equal("message_0.db", loaded.Metadata.DatabasePath);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SameMetadataReplacesOneRecordAtomically()
    {
        var vault = new ValidatedKeyVault(_root, new XorTestProtector());
        var metadata = Metadata("message_0.db");
        var first = Enumerable.Repeat((byte)1, 32).ToArray();
        var second = Enumerable.Repeat((byte)2, 32).ToArray();

        var firstId = vault.Store(metadata, first);
        var secondId = vault.Store(metadata, second);

        Assert.Equal(firstId, secondId);
        Assert.Single(Directory.EnumerateFiles(_root, "*.vkey"));
        using var loaded = vault.Load(secondId);
        Assert.Equal(second, loaded.Key);
    }

    private static ValidatedDatabaseKeyMetadata Metadata(string path) =>
        new(
            path,
            new DatabaseFileGeneration(4096, DateTime.UnixEpoch, "file-id"),
            "ps4096-kdf512-hmac512-le",
            "sqlite3_key_equiv");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            var result = input.ToArray();
            for (var index = 0; index < result.Length; index++) result[index] ^= 0xA5;
            return result;
        }
    }
}
