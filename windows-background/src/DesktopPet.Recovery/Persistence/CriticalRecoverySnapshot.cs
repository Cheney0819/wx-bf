using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;
using DesktopPet.Recovery.Security;

namespace DesktopPet.Recovery.Persistence;

public sealed record CriticalRecoveryState(
    string EpochId,
    RecoveryEpochIdentity Identity,
    int RestartCount,
    bool ActiveRestartSuppressed,
    RecoveryMode Mode,
    string? FailureCode,
    DateTimeOffset UpdatedAtUtc);

public interface ICriticalRecoverySnapshotStore
{
    Task SaveAsync(CriticalRecoveryState state, CancellationToken cancellationToken);

    Task<CriticalRecoveryState?> LoadAsync(CancellationToken cancellationToken);
}

public sealed class CriticalRecoverySnapshotStore : ICriticalRecoverySnapshotStore
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("JunjieeDesktopPet/Recovery/CriticalState/v1"));

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public CriticalRecoverySnapshotStore(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public async Task SaveAsync(
        CriticalRecoveryState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = _protector.Protect(plaintext, Entropy);
            await AtomicFile.ReplaceAsync(_path, ciphertext, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task<CriticalRecoveryState?> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        var ciphertext = await File.ReadAllBytesAsync(_path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(ciphertext, Entropy);
            return JsonSerializer.Deserialize<CriticalRecoveryState>(plaintext) ??
                throw new CryptographicException("Critical recovery state is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
