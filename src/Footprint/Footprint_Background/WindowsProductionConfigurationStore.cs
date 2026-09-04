using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Footprint.Core;

namespace Footprint.Background;

public sealed class WindowsProductionConfigurationStore
{
    private const string Schema = "footprint.windows-production.v1";
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly byte[] ProtectionEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Footprint.WindowsProductionConfiguration.v1"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _directory;
    private readonly string _baseDirectory;
    private readonly Func<string, string?> _environment;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public WindowsProductionConfigurationStore(string stateDirectory, string baseDirectory)
        : this(stateDirectory, baseDirectory, Environment.GetEnvironmentVariable,
            value => ProtectedKeyStore.Protect(value, ProtectionEntropy),
            value => ProtectedKeyStore.Unprotect(value, ProtectionEntropy))
    {
    }

    public WindowsProductionConfigurationStore(
        string stateDirectory,
        string baseDirectory,
        Func<string, string?> environment,
        Func<byte[], byte[]> protect,
        Func<byte[], byte[]> unprotect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "Footprint_Production");
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
    }

    public WindowsProductionConfiguration LoadOrProvision()
    {
        var configurationPath = Path.Combine(_directory, "production.json");
        if (!File.Exists(configurationPath)) Provision(configurationPath);
        return Load(configurationPath);
    }

    private void Provision(string configurationPath)
    {
        var server = RequiredEnvironment("FOOTPRINT_SERVER_BASE_URI");
        var uploadToken = RequiredEnvironment("FOOTPRINT_UPLOAD_TOKEN");
        var commandToken = RequiredEnvironment("FOOTPRINT_SOURCE_COMMAND_TOKEN");
        var receiptSource = ResolveKeySource("FOOTPRINT_RECEIPT_PUBLIC_KEY_PATH");
        var commandSource = ResolveKeySource("FOOTPRINT_COMMAND_PUBLIC_KEY_PATH");
        ValidateServer(server);
        ValidateKeySource(receiptSource);
        ValidateKeySource(commandSource);

        Directory.CreateDirectory(_directory);
        var receiptName = "receipt-public.pem";
        var commandName = "command-public.pem";
        var uploadName = "upload-token.dpapi";
        var commandTokenName = "command-token.dpapi";
        var uploadBytes = Encoding.UTF8.GetBytes(uploadToken);
        var commandBytes = Encoding.UTF8.GetBytes(commandToken);
        byte[]? protectedUpload = null;
        byte[]? protectedCommand = null;
        try
        {
            protectedUpload = _protect(uploadBytes);
            protectedCommand = _protect(commandBytes);
            WriteAtomic(Path.Combine(_directory, receiptName), File.ReadAllBytes(Path.GetFullPath(receiptSource)));
            WriteAtomic(Path.Combine(_directory, commandName), File.ReadAllBytes(Path.GetFullPath(commandSource)));
            WriteAtomic(Path.Combine(_directory, uploadName), protectedUpload);
            WriteAtomic(Path.Combine(_directory, commandTokenName), protectedCommand);
            var persisted = new PersistedConfiguration(
                Schema, server, receiptName, commandName, uploadName, commandTokenName);
            WriteAtomic(configurationPath, JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uploadBytes);
            CryptographicOperations.ZeroMemory(commandBytes);
            if (protectedUpload is not null) CryptographicOperations.ZeroMemory(protectedUpload);
            if (protectedCommand is not null) CryptographicOperations.ZeroMemory(protectedCommand);
        }
    }

    private WindowsProductionConfiguration Load(string configurationPath)
    {
        var info = new FileInfo(configurationPath);
        if (info.Length is <= 0 or > MaximumConfigurationBytes)
            throw new InvalidDataException("Windows production configuration size is invalid.");
        var persisted = JsonSerializer.Deserialize<PersistedConfiguration>(File.ReadAllBytes(configurationPath),
                            JsonOptions) ??
                        throw new InvalidDataException("Windows production configuration is empty.");
        if (!string.Equals(persisted.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidDataException("Windows production configuration schema is invalid.");
        ValidateServer(persisted.ServerBaseUri);
        var receiptPath = ResolveLeaf(persisted.ReceiptPublicKeyFile);
        var commandPath = ResolveLeaf(persisted.CommandPublicKeyFile);
        var uploadPath = ResolveLeaf(persisted.UploadTokenFile);
        var commandTokenPath = ResolveLeaf(persisted.CommandTokenFile);
        ValidateKeySource(receiptPath);
        ValidateKeySource(commandPath);
        var upload = _unprotect(File.ReadAllBytes(uploadPath));
        var command = _unprotect(File.ReadAllBytes(commandTokenPath));
        try
        {
            var uploadToken = StrictUtf8(upload);
            var commandToken = StrictUtf8(command);
            if (string.IsNullOrWhiteSpace(uploadToken) || string.IsNullOrWhiteSpace(commandToken))
                throw new InvalidDataException("Windows production credentials are empty.");
            return new WindowsProductionConfiguration(
                persisted.ServerBaseUri,
                uploadToken,
                commandToken,
                receiptPath,
                commandPath,
                Path.Combine(_baseDirectory, "Footprint_CaptureRuntime.dll"),
                Path.Combine(_baseDirectory, "Footprint_Capture.exe"),
                Path.Combine(_baseDirectory, "Footprint_Transfer.exe"),
                TimeSpan.FromSeconds(15));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(upload);
            CryptographicOperations.ZeroMemory(command);
        }
    }

    private string RequiredEnvironment(string name) =>
        _environment(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for first installation.");

    private string ResolveKeySource(string environmentName)
    {
        var envPath = _environment(environmentName);
        if (envPath is { Length: > 0 } && File.Exists(Path.GetFullPath(envPath)))
            return envPath;
        var bootstrapName = environmentName switch
        {
            "FOOTPRINT_RECEIPT_PUBLIC_KEY_PATH" => "receipt-public.pem",
            "FOOTPRINT_COMMAND_PUBLIC_KEY_PATH" => "command-public.pem",
            _ => throw new ArgumentException($"Unsupported key environment: {environmentName}")
        };
        var bootstrapPath = Path.Combine(Path.GetDirectoryName(_directory)!, "Footprint_ProductionBootstrap", bootstrapName);
        if (File.Exists(bootstrapPath))
            return bootstrapPath;
        return envPath ?? throw new InvalidOperationException($"{environmentName} is required for first installation.");
    }

    private string ResolveLeaf(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || Path.IsPathRooted(relativeName) ||
            relativeName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            relativeName is "." or "..")
            throw new InvalidDataException("Windows production configuration contains an invalid file name.");
        var path = Path.GetFullPath(Path.Combine(_directory, relativeName));
        if (!string.Equals(Path.GetDirectoryName(path), _directory, PathComparison()))
            throw new InvalidDataException("Windows production configuration escapes its state directory.");
        return path;
    }

    private static void ValidateServer(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Windows production server URI must use HTTPS.");
    }

    private static void ValidateKeySource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Windows production public key file is missing or unsafe.");
    }

    private static string StrictUtf8(byte[] value) => new UTF8Encoding(false, true).GetString(value);

    private static void WriteAtomic(string path, byte[] value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PersistedConfiguration(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("serverBaseUri")] string ServerBaseUri,
        [property: JsonPropertyName("receiptPublicKeyFile")] string ReceiptPublicKeyFile,
        [property: JsonPropertyName("commandPublicKeyFile")] string CommandPublicKeyFile,
        [property: JsonPropertyName("uploadTokenFile")] string UploadTokenFile,
        [property: JsonPropertyName("commandTokenFile")] string CommandTokenFile);
}
