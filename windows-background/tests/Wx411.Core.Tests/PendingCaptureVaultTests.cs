using System.Text;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class PendingCaptureVaultTests
{
    [Fact]
    public void WindowsProtectorNormalizesNativeFailuresForVaultCleanup()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "WindowsDpapiProtector.cs"));

        Assert.Contains("throw new CryptographicException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new Win32Exception", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SavesEncryptedPayloadAndLoadsMatchingSalt()
    {
        WithVault((root, vault) =>
        {
            var payload = Encoding.ASCII.GetBytes("x'00112233445566778899aabbccddeeff'");
            var recordId = vault.Save("salt-a", "module-a", "sqlite3_key_equiv", payload);

            var file = Assert.Single(Directory.GetFiles(root, "*.capture", SearchOption.AllDirectories));
            var serialized = File.ReadAllText(file);
            Assert.DoesNotContain(Encoding.ASCII.GetString(payload), serialized, StringComparison.Ordinal);

            var record = Assert.Single(vault.LoadMatching("salt-a", "module-a"));
            using (record)
            {
                Assert.Equal(recordId, record.RecordId);
                Assert.Equal("sqlite3_key_equiv", record.CallpointName);
                Assert.Equal(payload, record.CapturedPayload);
            }
        });
    }

    [Fact]
    public void DifferentSaltDoesNotLoadOrDeleteRecord()
    {
        WithVault((root, vault) =>
        {
            vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);

            Assert.Empty(vault.LoadMatching("salt-b", "module-a"));
            Assert.Single(Directory.GetFiles(root, "*.capture", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void CorruptCiphertextIsDeleted()
    {
        WithVault((root, vault) =>
        {
            vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);
            var file = Assert.Single(Directory.GetFiles(root, "*.capture", SearchOption.AllDirectories));
            File.WriteAllText(file, "{broken");

            Assert.Empty(vault.LoadMatching("salt-a", "module-a"));
            Assert.False(File.Exists(file));
        });
    }

    [Fact]
    public void NullCiphertextRecordIsDeletedWithoutDiscardingValidRecords()
    {
        WithVault((root, vault) =>
        {
            vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);
            var directory = Assert.Single(Directory.GetDirectories(root));
            var malformed = Path.Combine(directory, "bad.capture");
            File.WriteAllText(
                malformed,
                """{"Version":1,"DatabaseSaltFingerprint":"salt-a","ModuleSha256":"module-a","CallpointName":"cp","CapturedAtUtc":"2026-07-24T00:00:00Z","Ciphertext":null}""");

            var record = Assert.Single(vault.LoadMatching("salt-a", "module-a"));
            using (record) Assert.Equal([1, 2, 3, 4], record.CapturedPayload);
            Assert.False(File.Exists(malformed));
        });
    }

    [Fact]
    public void UnexpectedLoadFailureZeroesRecordsAlreadyDecrypted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-vault-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var protector = new FailsSecondUnprotectProtector();
            var vault = new PendingCaptureVault(root, protector);
            vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);
            vault.Save("salt-a", "module-a", "cp", [5, 6, 7, 8]);

            Assert.Throws<InvalidOperationException>(() =>
                vault.LoadMatching("salt-a", "module-a"));

            var firstPayload = Assert.IsType<byte[]>(protector.FirstUnprotectedPayload);
            Assert.All(firstPayload, value => Assert.Equal(0, value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteRemovesOnlyRequestedRecord()
    {
        WithVault((root, vault) =>
        {
            var first = vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);
            var second = vault.Save("salt-a", "module-a", "cp", [5, 6, 7, 8]);

            vault.Delete("salt-a", first);

            var record = Assert.Single(vault.LoadMatching("salt-a", "module-a"));
            using (record) Assert.Equal(second, record.RecordId);
            Assert.Single(Directory.GetFiles(root, "*.capture", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void DisposingRecordZeroesDecryptedPayload()
    {
        WithVault((_, vault) =>
        {
            vault.Save("salt-a", "module-a", "cp", [9, 8, 7, 6]);
            var record = Assert.Single(vault.LoadMatching("salt-a", "module-a"));
            var payload = record.CapturedPayload;

            record.Dispose();

            Assert.All(payload, value => Assert.Equal(0, value));
        });
    }

    [Fact]
    public void SnapshotRecordIdsReturnsOnlySortedDistinctValidIdsWithoutUnprotecting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-vault-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var protector = new ThrowingUnprotectProtector();
            var vault = new PendingCaptureVault(root, protector);
            var first = vault.Save("salt-a", "module-a", "cp", [1, 2, 3, 4]);
            var second = vault.Save("salt-b", "module-a", "cp", [5, 6, 7, 8]);
            var malformedDirectory = Path.Combine(root, "malformed");
            Directory.CreateDirectory(malformedDirectory);
            File.WriteAllText(Path.Combine(malformedDirectory, "not-an-id.capture"), "ignored");
            File.WriteAllText(Path.Combine(malformedDirectory, new string('g', 64) + ".capture"), "ignored");
            File.WriteAllText(Path.Combine(malformedDirectory, new string('a', 63) + ".capture"), "ignored");

            var ids = vault.SnapshotRecordIds();

            Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), ids);
            Assert.Equal(0, protector.UnprotectCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotRecordIdsReturnsEmptyWhenVaultRootIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-vault-missing-{Guid.NewGuid():N}");
        var vault = new PendingCaptureVault(root, new XorProtector());

        Assert.Empty(vault.SnapshotRecordIds());
    }

    [Fact]
    public void SnapshotRecordIdsPropagatesEnumerationFailure()
    {
        var vault = new PendingCaptureVault(
            Path.GetTempPath(),
            new XorProtector(),
            _ => throw new IOException("synthetic enumeration failure"));

        Assert.Throws<IOException>(() => vault.SnapshotRecordIds());
    }

    private static void WithVault(Action<string, PendingCaptureVault> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(root, new PendingCaptureVault(root, new XorProtector()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class XorProtector : ICapturePayloadProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext, entropy);

        private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy)
        {
            var result = new byte[input.Length];
            for (var index = 0; index < input.Length; index++)
                result[index] = (byte)(input[index] ^ 0xA5 ^ entropy[index % entropy.Length]);
            return result;
        }
    }

    private sealed class FailsSecondUnprotectProtector : ICapturePayloadProtector
    {
        private int _unprotectCalls;

        internal byte[]? FirstUnprotectedPayload { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            if (++_unprotectCalls == 2)
                throw new InvalidOperationException("synthetic protector failure");
            FirstUnprotectedPayload = ciphertext.ToArray();
            return FirstUnprotectedPayload;
        }
    }

    private sealed class ThrowingUnprotectProtector : ICapturePayloadProtector
    {
        internal int UnprotectCalls { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            UnprotectCalls++;
            throw new InvalidOperationException("Snapshot must not unprotect vault payloads.");
        }
    }
}
