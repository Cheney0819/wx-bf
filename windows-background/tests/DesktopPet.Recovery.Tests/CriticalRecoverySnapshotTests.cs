using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;

namespace DesktopPet.Recovery.Tests;

public sealed class CriticalRecoverySnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-critical-snapshot-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SnapshotIsProtectedAndAtomicallyReplaced()
    {
        var path = Path.Combine(_root, "critical-state.dpapi");
        var store = new CriticalRecoverySnapshotStore(
            path,
            new XorTestProtector());
        var state = new CriticalRecoveryState(
            "epoch-1",
            new RecoveryEpochIdentity("4.1.0", "root-a"),
            RestartCount: 2,
            ActiveRestartSuppressed: true,
            RecoveryMode.CaptureCircuitOpen,
            "zero_key",
            DateTimeOffset.UnixEpoch);

        await store.SaveAsync(state, default);
        var loaded = await store.LoadAsync(default);

        Assert.Equal(state, loaded);
        Assert.DoesNotContain("epoch-1", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

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
            for (var index = 0; index < result.Length; index++) result[index] ^= 0x5A;
            return result;
        }
    }
}
