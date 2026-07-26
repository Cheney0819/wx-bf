using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Upload;

namespace DesktopPet.DataSync.Tests;

public sealed class ServerSettingsBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-settings-bootstrap-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnvironmentPairIsNormalizedSavedAndReopened()
    {
        var fixture = CreateFixture(
            new Dictionary<string, string?>
            {
                ["WECHAT_MONITOR_SERVER_URL"] = "https://env.example/api/messages",
                ["WECHAT_MONITOR_SERVER_TOKEN"] = "env-token",
            });

        var result = await fixture.Bootstrapper.EnsureAsync(default);
        var reopened = await fixture.Vault.TryLoadAsync(default);

        Assert.Equal(ServerSettingsSource.Environment, result.Source);
        Assert.True(result.WasCreated);
        Assert.Equal(new Uri("https://env.example/"), result.Settings.BaseUri);
        Assert.Equal("env-token", reopened!.Token);
        var protectedBytes = await File.ReadAllBytesAsync(fixture.SettingsPath);
        Assert.Equal(-1, protectedBytes.AsSpan().IndexOf("env-token"u8));
        Assert.Equal(-1, protectedBytes.AsSpan().IndexOf("env.example"u8));
    }

    [Fact]
    public async Task ExistingVaultWinsOverEveryMigrationSource()
    {
        var fixture = CreateFixture(
            new Dictionary<string, string?>
            {
                ["WECHAT_MONITOR_SERVER_URL"] = "https://env.example/api/messages",
                ["WECHAT_MONITOR_SERVER_TOKEN"] = "env-token",
            });
        await fixture.Vault.SaveAsync(
            new ServerSettings(new Uri("https://existing.example/"), "existing-token"),
            default);

        var result = await fixture.Bootstrapper.EnsureAsync(default);

        Assert.Equal(ServerSettingsSource.ExistingVault, result.Source);
        Assert.False(result.WasCreated);
        Assert.Equal(new Uri("https://existing.example/"), result.Settings.BaseUri);
    }

    [Fact]
    public async Task LegacyJsonIsUsedWhenEnvironmentPairIsIncomplete()
    {
        Directory.CreateDirectory(_root);
        var legacyPath = Path.Combine(_root, "monitor_config.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "ServerUrl": "https://legacy.example/api/messages",
              "ServerToken": "legacy-token"
            }
            """);
        var fixture = CreateFixture(
            new Dictionary<string, string?>
            {
                ["WECHAT_MONITOR_SERVER_URL"] = "https://incomplete.example/api/messages",
            },
            [legacyPath]);

        var result = await fixture.Bootstrapper.EnsureAsync(default);

        Assert.Equal(ServerSettingsSource.LegacyJson, result.Source);
        Assert.Equal(new Uri("https://legacy.example/"), result.Settings.BaseUri);
        Assert.Equal("legacy-token", result.Settings.Token);
    }

    [Fact]
    public async Task DeploymentDefaultsMakeFreshInstallImmediatelyOnline()
    {
        var fixture = CreateFixture(new Dictionary<string, string?>());

        var result = await fixture.Bootstrapper.EnsureAsync(default);

        Assert.Equal(ServerSettingsSource.DeploymentDefault, result.Source);
        Assert.True(result.WasCreated);
        Assert.Equal(new Uri("https://wx.junjiee.online/"), result.Settings.BaseUri);
        Assert.Equal("wx_monitor_2026", result.Settings.Token);
    }

    [Fact]
    public async Task MissingMigrationSourcesRemainUnconfiguredWithoutDeploymentSecret()
    {
        var settingsPath = Path.Combine(_root, "server-settings.dpapi");
        var vault = new ServerSettingsVault(settingsPath, new XorProtector());
        var bootstrapper = new ServerSettingsBootstrapper(
            vault,
            settingsPath,
            [],
            _ => null,
            new ServerSettings(new Uri("https://unused.invalid/"), "unused"));

        var result = await bootstrapper.TryEnsureWithoutDefaultAsync(default);

        Assert.Null(result);
        Assert.False(File.Exists(settingsPath));
        Assert.Null(await vault.TryLoadAsync(default));
    }

    [Fact]
    public async Task ExplicitEnvironmentCredentialsReplaceAnExistingVault()
    {
        var fixture = CreateFixture(
            new Dictionary<string, string?>
            {
                ["WECHAT_MONITOR_SERVER_URL"] = "https://replacement.example/api/messages",
                ["WECHAT_MONITOR_SERVER_TOKEN"] = "replacement-token",
            });
        await fixture.Vault.SaveAsync(
            new ServerSettings(new Uri("https://existing.example/"), "expired-token"),
            default);

        var result = await fixture.Bootstrapper.TryEnsureWithoutDefaultAsync(default);
        var reopened = await fixture.Vault.TryLoadAsync(default);

        Assert.NotNull(result);
        Assert.Equal(ServerSettingsSource.Environment, result.Source);
        Assert.True(result.WasCreated);
        Assert.Equal(new Uri("https://replacement.example/"), reopened!.BaseUri);
        Assert.Equal("replacement-token", reopened.Token);
    }

    [Fact]
    public async Task UnsafeEnvironmentPairFallsBackToDeploymentDefaults()
    {
        var fixture = CreateFixture(
            new Dictionary<string, string?>
            {
                ["WECHAT_MONITOR_SERVER_URL"] = "http://remote.example/api/messages",
                ["WECHAT_MONITOR_SERVER_TOKEN"] = "unsafe-token",
            });

        var result = await fixture.Bootstrapper.EnsureAsync(default);

        Assert.Equal(ServerSettingsSource.DeploymentDefault, result.Source);
        Assert.Equal(new Uri("https://wx.junjiee.online/"), result.Settings.BaseUri);
    }

    [Fact]
    public async Task CorruptVaultIsQuarantinedBeforeDefaultsAreSaved()
    {
        var fixture = CreateFixture(new Dictionary<string, string?>());
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.SettingsPath)!);
        await File.WriteAllBytesAsync(fixture.SettingsPath, [1, 2, 3, 4]);

        var result = await fixture.Bootstrapper.EnsureAsync(default);

        Assert.Equal(ServerSettingsSource.DeploymentDefault, result.Source);
        Assert.True(File.Exists(Path.Combine(_root, "server-settings.invalid")));
        Assert.NotNull(await fixture.Vault.TryLoadAsync(default));
    }

    [Fact]
    public async Task ConcurrentCallersCreateOnlyOneVault()
    {
        var fixture = CreateFixture(new Dictionary<string, string?>());

        var results = await Task.WhenAll(
            fixture.Bootstrapper.EnsureAsync(default),
            fixture.Bootstrapper.EnsureAsync(default));

        Assert.Single(results, result => result.WasCreated);
        Assert.Single(results, result =>
            result.Source == ServerSettingsSource.ExistingVault);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string>? legacyPaths = null)
    {
        var settingsPath = Path.Combine(_root, "server-settings.dpapi");
        var vault = new ServerSettingsVault(settingsPath, new XorProtector());
        var defaults = new ServerSettings(
            new Uri("https://wx.junjiee.online/"),
            "wx_monitor_2026");
        var bootstrapper = new ServerSettingsBootstrapper(
            vault,
            settingsPath,
            legacyPaths ?? [],
            name => environment.TryGetValue(name, out var value) ? value : null,
            defaults);
        return new Fixture(settingsPath, vault, bootstrapper);
    }

    private sealed record Fixture(
        string SettingsPath,
        ServerSettingsVault Vault,
        ServerSettingsBootstrapper Bootstrapper);

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(
            ReadOnlySpan<byte> plaintext,
            ReadOnlySpan<byte> entropy) =>
            Transform(plaintext);

        public byte[] Unprotect(
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext);

        private static byte[] Transform(ReadOnlySpan<byte> value)
        {
            var result = value.ToArray();
            for (var index = 0; index < result.Length; index++)
                result[index] ^= 0xA5;
            return result;
        }
    }
}
