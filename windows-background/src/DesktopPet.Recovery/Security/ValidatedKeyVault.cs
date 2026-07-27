using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Infrastructure;
using Wx411.Core;

namespace DesktopPet.Recovery.Security;

public sealed class ValidatedKeyRecord : IDisposable
{
    private byte[]? _key;

    internal ValidatedKeyRecord(
        string id,
        ValidatedDatabaseKeyMetadata metadata,
        byte[] key)
    {
        Id = id;
        Metadata = metadata;
        _key = key;
    }

    public string Id { get; }

    public ValidatedDatabaseKeyMetadata Metadata { get; }

    public byte[] Key => _key ?? throw new ObjectDisposedException(nameof(ValidatedKeyRecord));

    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }
}

public sealed class ValidatedKeyVault : IValidatedDatabaseKeySink
{
    private const int CurrentVersion = 1;
    private const int HeaderLength = 12;
    private static readonly byte[] Magic = "DPKV"u8.ToArray();
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("JunjieeDesktopPet/Recovery/ValidatedKeyVault/v1"));

    private readonly string _root;
    private readonly ISecretProtector _protector;

    public ValidatedKeyVault(string root, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(protector);
        _root = Path.GetFullPath(root);
        _protector = protector;
    }

    void IValidatedDatabaseKeySink.Store(
        ValidatedDatabaseKeyMetadata metadata,
        ReadOnlySpan<byte> key) => Store(metadata, key);

    public string Store(ValidatedDatabaseKeyMetadata metadata, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (key.Length != 32) throw new ArgumentException("Validated key must be 32 bytes.", nameof(key));

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var idBytes = SHA256.HashData(metadataBytes);
        var id = Convert.ToHexString(idBytes).ToLowerInvariant();
        var plaintext = new byte[checked(8 + metadataBytes.Length + key.Length)];
        byte[]? ciphertext = null;
        byte[]? envelope = null;
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(plaintext, metadataBytes.Length);
            metadataBytes.CopyTo(plaintext.AsSpan(4));
            var keyLengthOffset = 4 + metadataBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(
                plaintext.AsSpan(keyLengthOffset),
                key.Length);
            key.CopyTo(plaintext.AsSpan(keyLengthOffset + 4));

            ciphertext = _protector.Protect(plaintext, Entropy);
            envelope = new byte[checked(HeaderLength + ciphertext.Length)];
            Magic.CopyTo(envelope, 0);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(4), CurrentVersion);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(8), ciphertext.Length);
            ciphertext.CopyTo(envelope, HeaderLength);
            AtomicFile.Replace(PathFor(id), envelope);
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataBytes);
            CryptographicOperations.ZeroMemory(idBytes);
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public ValidatedKeyRecord Load(string id)
    {
        ValidateId(id);
        var envelope = File.ReadAllBytes(PathFor(id));
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            if (envelope.Length < HeaderLength || !envelope.AsSpan(0, 4).SequenceEqual(Magic))
                throw new CryptographicException("Validated key envelope header is invalid.");
            var version = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(4, 4));
            var cipherLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(8, 4));
            if (version != CurrentVersion || cipherLength <= 0 || cipherLength != envelope.Length - HeaderLength)
                throw new CryptographicException("Validated key envelope length or version is invalid.");

            ciphertext = envelope.AsSpan(HeaderLength).ToArray();
            plaintext = _protector.Unprotect(ciphertext, Entropy);
            if (plaintext.Length < 8) throw new CryptographicException("Validated key payload is truncated.");
            var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext.AsSpan(0, 4));
            if (metadataLength <= 0 || metadataLength > plaintext.Length - 8)
                throw new CryptographicException("Validated key metadata length is invalid.");
            var keyLengthOffset = 4 + metadataLength;
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(
                plaintext.AsSpan(keyLengthOffset, 4));
            if (keyLength != 32 || keyLengthOffset + 4 + keyLength != plaintext.Length)
                throw new CryptographicException("Validated key length is invalid.");

            var metadata = JsonSerializer.Deserialize<ValidatedDatabaseKeyMetadata>(
                plaintext.AsSpan(4, metadataLength)) ??
                throw new CryptographicException("Validated key metadata is invalid.");
            var key = plaintext.AsSpan(keyLengthOffset + 4, keyLength).ToArray();
            return new ValidatedKeyRecord(id, metadata, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public IReadOnlyList<string> ListIds()
    {
        if (!Directory.Exists(_root)) return [];
        return Array.AsReadOnly(Directory
            .EnumerateFiles(_root, "*.vkey", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is { Length: 64 } && id.All(Uri.IsHexDigit))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray());
    }

    public void Quarantine(string id)
    {
        ValidateId(id);
        var source = PathFor(id);
        if (!File.Exists(source)) return;
        try
        {
            var quarantineRoot = Path.Combine(_root, "quarantine");
            Directory.CreateDirectory(quarantineRoot);
            var destination = Path.Combine(
                quarantineRoot,
                $"{id}.{DateTime.UtcNow.Ticks:X16}.{Guid.NewGuid():N}.quarantine");
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException)
        {
            // A concurrent reader may have already quarantined this record.
        }
        catch (UnauthorizedAccessException)
        {
            // The malformed record remains isolated by the caller for this pass.
        }
    }

    private string PathFor(string id) => Path.Combine(_root, id + ".vkey");

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Validated key ID must be a SHA-256 hex string.", nameof(id));
    }
}
