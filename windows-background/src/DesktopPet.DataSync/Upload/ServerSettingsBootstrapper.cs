using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopPet.DataSync.Upload;

public enum ServerSettingsSource
{
    ExistingVault,
    Environment,
    LegacyJson,
    DeploymentDefault,
}

public sealed record ServerSettingsBootstrapResult(
    ServerSettings Settings,
    ServerSettingsSource Source,
    bool WasCreated);

public sealed class ServerSettingsBootstrapper
{
    private const int MaximumLegacyConfigBytes = 64 * 1024;
    private readonly ServerSettingsVault _vault;
    private readonly string _settingsPath;
    private readonly IReadOnlyList<string> _legacyConfigPaths;
    private readonly Func<string, string?> _readEnvironment;
    private readonly ServerSettings? _deploymentDefaults;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerSettingsBootstrapper(
        ServerSettingsVault vault,
        string settingsPath,
        IEnumerable<string> legacyConfigPaths,
        Func<string, string?> readEnvironment,
        ServerSettings? deploymentDefaults)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(legacyConfigPaths);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        _vault = vault;
        _settingsPath = Path.GetFullPath(settingsPath);
        _legacyConfigPaths = legacyConfigPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _readEnvironment = readEnvironment;
        _deploymentDefaults = deploymentDefaults;
    }

    public async Task<ServerSettingsBootstrapResult> EnsureAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await EnsureCoreAsync(_deploymentDefaults, cancellationToken);
            return result ?? throw new InvalidOperationException(
                "No server credentials are configured.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerSettingsBootstrapResult?> TryEnsureWithoutDefaultAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureCoreAsync(null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ServerSettingsBootstrapResult?> EnsureCoreAsync(
        ServerSettings? fallback,
        CancellationToken cancellationToken)
    {
        var existing = await TryLoadExistingAsync(cancellationToken);
        if (existing is not null)
        {
            return new ServerSettingsBootstrapResult(
                existing,
                ServerSettingsSource.ExistingVault,
                WasCreated: false);
        }

        var source = ServerSettingsSource.DeploymentDefault;
        var selected = TryReadEnvironment();
        if (selected is not null)
        {
            source = ServerSettingsSource.Environment;
        }
        else
        {
            selected = await TryReadLegacyAsync(cancellationToken);
            if (selected is not null)
                source = ServerSettingsSource.LegacyJson;
        }
        selected ??= fallback;
        if (selected is null) return null;

        await _vault.SaveAsync(selected, cancellationToken);
        var reopened = await _vault.TryLoadAsync(cancellationToken) ??
            throw new CryptographicException(
                "Protected server settings were not persisted.");
        return new ServerSettingsBootstrapResult(
            reopened,
            source,
            WasCreated: true);
    }

    private async Task<ServerSettings?> TryLoadExistingAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _vault.TryLoadAsync(cancellationToken);
        }
        catch (CryptographicException)
        {
            QuarantineInvalidVault();
            return null;
        }
    }

    private ServerSettings? TryReadEnvironment()
    {
        var endpoint = _readEnvironment("WECHAT_MONITOR_SERVER_URL");
        var token = _readEnvironment("WECHAT_MONITOR_SERVER_TOKEN");
        return TryCreate(endpoint, token);
    }

    private async Task<ServerSettings?> TryReadLegacyAsync(
        CancellationToken cancellationToken)
    {
        foreach (var path in _legacyConfigPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumLegacyConfigBytes) continue;
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    MaximumLegacyConfigBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                var endpoint = ReadString(document.RootElement, "ServerUrl");
                var token = ReadString(document.RootElement, "ServerToken");
                var settings = TryCreate(endpoint, token);
                if (settings is not null) return settings;
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ServerSettings? TryCreate(
        string? endpoint,
        string? token)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(token) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not "/" and not "/api/messages" ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            token.Trim().Length > 4096)
        {
            return null;
        }

        var builder = new UriBuilder(
            uri.Scheme,
            uri.Host,
            uri.IsDefaultPort ? -1 : uri.Port)
        {
            Path = "/",
            Query = "",
            Fragment = "",
        };
        return new ServerSettings(builder.Uri, token.Trim());
    }

    private void QuarantineInvalidVault()
    {
        if (!File.Exists(_settingsPath)) return;
        var invalidPath = Path.Combine(
            Path.GetDirectoryName(_settingsPath)!,
            "server-settings.invalid");
        try
        {
            File.Move(_settingsPath, invalidPath, overwrite: true);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
