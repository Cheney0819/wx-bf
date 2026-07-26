using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Upload;

public sealed class ServerSettingsVault : IServerSettingsProvider
{
    private const int MaximumProtectedBytes = 64 * 1024;
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("desktop-pet-datasync-server-settings-v1"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public ServerSettingsVault(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public async Task SaveAsync(
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var ciphertext = _protector.Protect(plaintext, Entropy);
        try
        {
            if (ciphertext.Length > MaximumProtectedBytes)
                throw new CryptographicException("Protected server settings exceed their size limit.");
            await AtomicFile.ReplaceAsync(_path, ciphertext, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumProtectedBytes)
            throw new CryptographicException("Protected server settings are invalid.");
        var ciphertext = await File.ReadAllBytesAsync(_path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(ciphertext, Entropy);
            ServerSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<ServerSettings>(plaintext, JsonOptions) ??
                    throw new CryptographicException("Protected server settings are empty.");
            }
            catch (JsonException exception)
            {
                throw new CryptographicException("Protected server settings failed validation.", exception);
            }
            Validate(settings);
            return settings;
        }
        catch (ArgumentException exception)
        {
            throw new CryptographicException("Protected server settings failed validation.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(ServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.BaseUri.IsAbsoluteUri ||
            !string.IsNullOrEmpty(settings.BaseUri.UserInfo) ||
            !string.IsNullOrEmpty(settings.BaseUri.Query) ||
            !string.IsNullOrEmpty(settings.BaseUri.Fragment) ||
            settings.BaseUri.AbsolutePath != "/" ||
            settings.BaseUri.Scheme != Uri.UriSchemeHttps &&
            !(settings.BaseUri.Scheme == Uri.UriSchemeHttp && settings.BaseUri.IsLoopback))
        {
            throw new ArgumentException("Server base URI must be a credential-free HTTPS origin.", nameof(settings));
        }
        if (string.IsNullOrWhiteSpace(settings.Token) || settings.Token.Length > 4096)
            throw new ArgumentException("Server token is invalid.", nameof(settings));
    }
}
