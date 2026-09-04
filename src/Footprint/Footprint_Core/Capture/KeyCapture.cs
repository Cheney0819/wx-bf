using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Footprint.Core.Capture;

public interface ICachedKeyStore
{
    Task<CachedKeyCandidate?> LoadAsync(string runId, CaptureGenerationId generation, string databaseTag,
        CancellationToken cancellationToken);

    Task SaveAsync(string runId, CaptureGenerationId generation, string databaseTag, ReadOnlyMemory<byte> keyBytes,
        CancellationToken cancellationToken);

    Task DeleteAsync(string runId, CaptureGenerationId generation, string databaseTag,
        CancellationToken cancellationToken);
}

public sealed class CachedKeyStoreException : Exception
{
    public CachedKeyStoreException(string code, string message, Exception? internalCause = null)
        : base(message, internalCause)
    {
        Code = code;
        InternalCause = internalCause;
    }

    public string Code { get; }
    internal Exception? InternalCause { get; }

    public override string ToString() => $"{GetType().FullName}: [{Code}] {Message}";
}

public static class CachedKeyIdentity
{
    public static string GenerationHash(CaptureGenerationId generation)
    {
        var entropy = GenerationEntropy(generation);
        try { return Convert.ToHexString(entropy).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    public static byte[] GenerationEntropy(CaptureGenerationId generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var budgetBytes = Encoding.UTF8.GetBytes(generation.BudgetKey);
        try { return SHA256.HashData(budgetBytes); }
        finally { CryptographicOperations.ZeroMemory(budgetBytes); }
    }

    public static string DatabaseTagHash(string databaseTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTag);
        var bytes = Encoding.UTF8.GetBytes(databaseTag);
        byte[]? hash = null;
        try
        {
            hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (hash is not null) CryptographicOperations.ZeroMemory(hash);
        }
    }
}

public sealed class CachedKeyCandidate : IDisposable
{
    private readonly byte[] _keyBytes;
    private bool _disposed;

    public CachedKeyCandidate(CaptureGenerationId generation, string databaseTag, byte[] keyBytes)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTag);
        ArgumentNullException.ThrowIfNull(keyBytes);
        if (!IsSupportedLength(keyBytes.Length)) throw new InvalidDataException("缓存数据库密钥长度无效。");

        Generation = generation;
        DatabaseTag = databaseTag;
        GenerationHash = CachedKeyIdentity.GenerationHash(generation);
        KeyLength = keyBytes.Length;
        _keyBytes = keyBytes.ToArray();
    }

    public CaptureGenerationId Generation { get; }
    public string DatabaseTag { get; }
    public string GenerationHash { get; }
    public int KeyLength { get; }

    [JsonIgnore]
    public bool IsDisposed => _disposed;

    public static bool IsSupportedLength(int length) => length is 32 or 67 or 99;

    internal byte[] CopyKeyForValidation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyBytes.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_keyBytes);
        _disposed = true;
    }

    public override string ToString() =>
        $"CachedKeyCandidate {{ DatabaseTag = {DatabaseTag}, GenerationHash = {GenerationHash}, KeyLength = {KeyLength}, IsDisposed = {IsDisposed} }}";
}

public sealed record CachedKeyValidationEvent(string Code, string MessageZh);

public sealed record VerifiedCachedKeyBinding(
    CaptureGenerationId Generation,
    string DatabasePath,
    string DatabaseTag,
    int KeyLength,
    int Compatibility,
    int PageSize,
    CachedKeyVerificationSummary VerificationSummary);

public sealed record CachedKeyVerificationSummary(
    bool Accepted,
    int Compatibility,
    int PageSize,
    string ReasonCode);

public sealed record CachedKeyValidationResult(
    bool Accepted,
    string Code,
    string MessageZh,
    VerifiedCachedKeyBinding? Binding,
    IReadOnlyList<CachedKeyValidationEvent> Events)
{
    public static CachedKeyValidationResult Failure(string code, string messageZh,
        IReadOnlyList<CachedKeyValidationEvent>? events = null) =>
        new(false, code, messageZh, null, events ?? []);
}

public static class CachedKeyValidator
{
    public static async Task<CachedKeyValidationResult> ValidateAsync(
        CaptureGenerationId generation,
        string databasePath,
        string databaseTag,
        CachedKeyCandidate candidate,
        SqlCipherVerifier verifier,
        string sqlCipherExecutable,
        int preferredCompatibility,
        int pageSize,
        string expectedCipherVersion,
        string verificationRoot,
        IProgress<CachedKeyValidationEvent>? progress = null,
        int snapshotAttempts = 3,
        Func<int, Task>? afterSnapshotCopyAttempt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTag);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlCipherExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCipherVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationRoot);

        var events = new List<CachedKeyValidationEvent>();
        byte[]? validationKey = null;
        string? operationDirectory = null;
        try
        {
            if (!string.Equals(candidate.GenerationHash, CachedKeyIdentity.GenerationHash(generation),
                    StringComparison.Ordinal) ||
                !string.Equals(candidate.DatabaseTag, databaseTag, StringComparison.Ordinal))
                return Failure("cached_key_generation_mismatch", "缓存数据库密钥已失效，需要重新捕获。");

            if (!CachedKeyCandidate.IsSupportedLength(candidate.KeyLength))
                return Failure("cached_key_length_invalid", "缓存数据库密钥长度无效。");

            Publish("cached_key_candidate_loaded", "正在验证缓存数据库密钥");
            validationKey = candidate.CopyKeyForValidation();
            operationDirectory = Path.Combine(Path.GetFullPath(verificationRoot), Guid.NewGuid().ToString("N"));
            var snapshotDatabase = await CreateFreshVerificationSnapshotAsync(databasePath, operationDirectory,
                snapshotAttempts, afterSnapshotCopyAttempt, cancellationToken);
            if (snapshotDatabase is null)
                return Failure("cached_key_snapshot_unstable", "数据库快照不稳定，缓存密钥未被采用。");

            var verdict = await verifier.VerifyAsync(sqlCipherExecutable, snapshotDatabase, validationKey,
                preferredCompatibility, pageSize, expectedCipherVersion, cancellationToken);
            if (!verdict.Accepted || verdict.Compatibility is null || verdict.PageSize is null)
                return Failure("cached_key_verification_rejected", "缓存数据库密钥验证未通过。");

            Publish("cached_key_verified", "缓存数据库密钥验证通过");
            var summary = new CachedKeyVerificationSummary(true, verdict.Compatibility.Value,
                verdict.PageSize.Value, "exactly_one_sqlcipher_configuration");
            var binding = new VerifiedCachedKeyBinding(generation, databasePath, databaseTag, candidate.KeyLength,
                verdict.Compatibility.Value, verdict.PageSize.Value, summary);
            return new CachedKeyValidationResult(true, "cached_key_verified", "缓存数据库密钥验证通过。", binding,
                events.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure("cached_key_verification_failed", "缓存数据库密钥验证失败。");
        }
        finally
        {
            if (validationKey is not null) CryptographicOperations.ZeroMemory(validationKey);
            candidate.Dispose();
            if (operationDirectory is not null) VerificationSnapshotCopy.Delete(operationDirectory);
            DeleteEmptyVerificationRoot(verificationRoot);
        }

        CachedKeyValidationResult Failure(string code, string messageZh)
        {
            Publish(code, messageZh);
            return CachedKeyValidationResult.Failure(code, messageZh, events.ToArray());
        }

        void Publish(string code, string messageZh)
        {
            var item = new CachedKeyValidationEvent(code, messageZh);
            events.Add(item);
            progress?.Report(item);
        }
    }

    private static async Task<string?> CreateFreshVerificationSnapshotAsync(string databasePath,
        string operationDirectory, int snapshotAttempts, Func<int, Task>? afterSnapshotCopyAttempt,
        CancellationToken cancellationToken)
    {
        if (snapshotAttempts < 1) throw new ArgumentOutOfRangeException(nameof(snapshotAttempts));
        Directory.CreateDirectory(operationDirectory);
        for (var attempt = 1; attempt <= snapshotAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptDirectory = Path.Combine(operationDirectory, $"attempt-{attempt:D2}-{Guid.NewGuid():N}");
            var snapshot = await StableSnapshotter.CreateAsync(databasePath, attemptDirectory, 1, cancellationToken,
                afterSnapshotCopyAttempt is null ? null : _ => afterSnapshotCopyAttempt(attempt));
            if (!snapshot.Stable)
            {
                VerificationSnapshotCopy.Delete(attemptDirectory);
                if (attempt < snapshotAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                continue;
            }

            var finalDirectory = Path.Combine(operationDirectory, $"final-{Guid.NewGuid():N}");
            try
            {
                var finalDatabase = await VerificationSnapshotCopy.CreateAsync(snapshot,
                    Path.GetFileName(databasePath), finalDirectory, cancellationToken);
                VerificationSnapshotCopy.Delete(attemptDirectory);
                return finalDatabase;
            }
            catch
            {
                VerificationSnapshotCopy.Delete(finalDirectory);
                throw;
            }
        }
        return null;
    }

    private static void DeleteEmptyVerificationRoot(string verificationRoot)
    {
        try
        {
            if (Directory.Exists(verificationRoot) && !Directory.EnumerateFileSystemEntries(verificationRoot).Any())
                Directory.Delete(verificationRoot, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
