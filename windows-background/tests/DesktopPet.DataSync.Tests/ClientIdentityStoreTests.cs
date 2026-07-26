using System.Text.Json;
using DesktopPet.DataSync.Identity;

namespace DesktopPet.DataSync.Tests;

public sealed class ClientIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-client-identity-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportsLegacyIdentityWithoutMutatingItsFile()
    {
        var legacyPath = Path.Combine(_root, "legacy", "client_identity.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        const string legacyJson = "{\"session_id\":\"client-cs-existing\",\"created_at\":\"2026-07-01T00:00:00Z\"}";
        await File.WriteAllTextAsync(legacyPath, legacyJson);
        var store = CreateStore(legacyPath);

        var identity = await store.GetAsync(default);

        Assert.Equal("client-cs-existing", identity.SessionId);
        Assert.Equal("client_cs", identity.Source);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(legacyPath));
    }

    [Fact]
    public async Task GeneratedIdentityIsStableAcrossStoreReopen()
    {
        var path = Path.Combine(_root, "client-identity.json");
        var first = await new ClientIdentityStore(path, null, TimeProvider.System)
            .GetAsync(default);
        var second = await new ClientIdentityStore(path, null, TimeProvider.System)
            .GetAsync(default);

        Assert.Equal(first, second);
        Assert.StartsWith("client-datasync-", first.SessionId, StringComparison.Ordinal);
        Assert.Equal("client_datasync", first.Source);
        Assert.Equal(1, first.SchemaVersion);
    }

    [Fact]
    public async Task CorruptPersistedIdentityIsRejectedInsteadOfRegenerated()
    {
        var path = Path.Combine(_root, "client-identity.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"sessionId\":\"bad id\"}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ClientIdentityStore(path, null, TimeProvider.System).GetAsync(default));
    }

    [Fact]
    public async Task OversizedPersistedIdentityIsRejected()
    {
        var path = Path.Combine(_root, "client-identity.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(path, new byte[64 * 1024 + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ClientIdentityStore(path, null, TimeProvider.System).GetAsync(default));
    }

    private ClientIdentityStore CreateStore(string? legacyPath) => new(
        Path.Combine(_root, "DataSync", "client-identity.json"),
        legacyPath,
        TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
