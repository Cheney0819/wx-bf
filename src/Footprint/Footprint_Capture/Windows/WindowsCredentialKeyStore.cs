using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Footprint.Core;
using Footprint.Core.Capture;
using Microsoft.Win32.SafeHandles;

namespace Footprint.Capture.Windows;

internal interface IWindowsCredentialApi
{
    byte[]? ReadGeneric(string target);
    void WriteGeneric(string target, ReadOnlySpan<byte> descriptor);
    void DeleteGeneric(string target);
}

internal interface IWindowsDataProtectionApi
{
    byte[] Protect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> entropy);
    byte[] Unprotect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> entropy);
}

internal interface IWindowsKeyFileApi
{
    IWindowsRootLease VerifyRoot(string requestedRoot, bool createIfMissing);
    void Recover(IWindowsRootLease root, string relativeName, string expectedSha256);
    bool Exists(IWindowsRootLease root, string relativeName);
    byte[] ReadAllBytes(IWindowsRootLease root, string relativeName);
    IWindowsAtomicSidecar BeginAtomicReplace(IWindowsRootLease root, string relativeName, ReadOnlySpan<byte> value);
    void Delete(IWindowsRootLease root, string relativeName);
}

internal interface IWindowsRootLease : IDisposable
{
    string CanonicalRoot { get; }
}

internal interface IWindowsAtomicSidecar : IDisposable
{
    void Commit();
    void Complete();
    void Rollback();
    void ConfirmRollback();
    bool RollbackSucceeded { get; }
    IReadOnlyList<Exception> Diagnostics { get; }
}

public sealed class WindowsCredentialKeyStore : ICachedKeyStore
{
    public const int GenericCredentialType = 1;
    private const int DescriptorVersion = 3;
    private readonly string _secretsDirectory;
    private readonly IWindowsCredentialApi _credentials;
    private readonly IWindowsDataProtectionApi _dpapi;
    private readonly IWindowsKeyFileApi _files;
    private readonly Func<bool> _isWindows;
    private readonly Action<string> _diagnosticSink;

    public WindowsCredentialKeyStore(string secretsDirectory)
        : this(secretsDirectory, new NativeWindowsCredentialApi(), new ProtectedKeyDataApi(),
            new NativeWindowsKeyFileApi(), OperatingSystem.IsWindows,
            static message => System.Diagnostics.Trace.TraceWarning(message))
    {
    }

    internal WindowsCredentialKeyStore(string secretsDirectory, IWindowsCredentialApi credentials,
        IWindowsDataProtectionApi dpapi, IWindowsKeyFileApi files, Func<bool> isWindows,
        Action<string>? diagnosticSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);
        _secretsDirectory = Path.GetFullPath(secretsDirectory);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _dpapi = dpapi ?? throw new ArgumentNullException(nameof(dpapi));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _diagnosticSink = diagnosticSink ?? (static message => System.Diagnostics.Trace.TraceWarning(message));
    }

    public static string BuildCredentialTarget(string secretsRoot, string runId, CaptureGenerationId generation,
        string databaseTag) => BuildContext(CanonicalizeRootForIdentity(secretsRoot), runId, generation, databaseTag).Target;

    public Task<CachedKeyCandidate?> LoadAsync(string runId, CaptureGenerationId generation, string databaseTag,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? descriptorBytes = null;
        byte[]? entropy = null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        IWindowsRootLease? root = null;
        try
        {
            root = _files.VerifyRoot(_secretsDirectory, createIfMissing: false);
            var context = BuildContext(root.CanonicalRoot, runId, generation, databaseTag);
            descriptorBytes = _credentials.ReadGeneric(context.Target);
            if (descriptorBytes is null) return Task.FromResult<CachedKeyCandidate?>(null);

            var descriptor = DeserializeDescriptor(descriptorBytes);
            if (!DescriptorMatches(descriptor, context)) return Task.FromResult<CachedKeyCandidate?>(null);
            _files.Recover(root, context.SidecarRelativeName, descriptor!.SidecarSha256);
            if (!_files.Exists(root, context.SidecarRelativeName))
                return Task.FromResult<CachedKeyCandidate?>(null);

            encrypted = _files.ReadAllBytes(root, context.SidecarRelativeName);
            if (!SidecarHashMatches(encrypted, descriptor.SidecarSha256))
                throw new CachedKeyStoreException("cached_key_sidecar_hash_mismatch", "缓存密钥文件校验失败。");
            entropy = CachedKeyIdentity.GenerationEntropy(generation);
            plaintext = _dpapi.Unprotect(encrypted, entropy);
            if (plaintext.Length != descriptor!.KeyLength || !CachedKeyCandidate.IsSupportedLength(plaintext.Length))
                throw new CachedKeyStoreException("cached_key_length_invalid", "缓存数据库密钥长度无效。");
            return Task.FromResult<CachedKeyCandidate?>(new CachedKeyCandidate(generation, databaseTag, plaintext));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CachedKeyStoreException)
        {
            throw;
        }
        catch (JsonException)
        {
            return Task.FromResult<CachedKeyCandidate?>(null);
        }
        catch (Exception error)
        {
            throw StoreFailure("cached_key_store_load_failed", "缓存数据库密钥读取失败。", error);
        }
        finally
        {
            try { TryDispose(root); }
            finally
            {
                Zero(descriptorBytes);
                Zero(entropy);
                Zero(encrypted);
                Zero(plaintext);
            }
        }
    }

    public Task SaveAsync(string runId, CaptureGenerationId generation, string databaseTag,
        ReadOnlyMemory<byte> keyBytes, CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        if (!CachedKeyCandidate.IsSupportedLength(keyBytes.Length))
            throw new CachedKeyStoreException("cached_key_length_invalid", "缓存数据库密钥长度无效。");

        byte[]? oldDescriptor = null;
        byte[]? descriptorBytes = null;
        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? encrypted = null;
        byte[]? encryptedHash = null;
        IWindowsRootLease? root = null;
        IWindowsAtomicSidecar? transaction = null;
        string? credentialTarget = null;
        var credentialWriteAttempted = false;
        try
        {
            root = _files.VerifyRoot(_secretsDirectory, createIfMissing: true);
            var context = BuildContext(root.CanonicalRoot, runId, generation, databaseTag);
            credentialTarget = context.Target;
            oldDescriptor = _credentials.ReadGeneric(context.Target);
            var previous = TryDeserializeDescriptor(oldDescriptor);
            if (DescriptorMatches(previous, context))
                _files.Recover(root, context.SidecarRelativeName, previous!.SidecarSha256);
            plaintext = keyBytes.ToArray();
            entropy = CachedKeyIdentity.GenerationEntropy(generation);
            encrypted = _dpapi.Protect(plaintext, entropy);
            encryptedHash = SHA256.HashData(encrypted);
            var descriptor = new CredentialDescriptor(DescriptorVersion, context.RootIdentityHash, runId,
                context.GenerationHash, context.DatabaseTagHash, context.SidecarRelativeName, keyBytes.Length,
                Convert.ToHexString(encryptedHash).ToLowerInvariant());
            descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);
            transaction = _files.BeginAtomicReplace(root, context.SidecarRelativeName, encrypted);
            transaction.Commit();
            credentialWriteAttempted = true;
            _credentials.WriteGeneric(context.Target, descriptorBytes);
            var diagnosticsBeforeComplete = transaction.Diagnostics.Count;
            transaction.Complete();
            if (transaction.Diagnostics.Count > diagnosticsBeforeComplete)
                ReportDiagnostic("缓存密钥文件事务清理未完成。");
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ReconcileFailedSave(transaction, credentialWriteAttempted, credentialTarget, oldDescriptor);
            throw;
        }
        catch (CachedKeyStoreException error)
        {
            var reconciliation = ReconcileFailedSave(transaction, credentialWriteAttempted, credentialTarget,
                oldDescriptor);
            if (reconciliation is null) throw;
            throw new CachedKeyStoreException(error.Code, error.Message,
                CombineInternal(error, reconciliation));
        }
        catch (Exception error)
        {
            var reconciliation = ReconcileFailedSave(transaction, credentialWriteAttempted, credentialTarget,
                oldDescriptor);
            throw StoreFailure("cached_key_store_save_failed", "缓存数据库密钥保存失败。",
                reconciliation is null ? error : CombineInternal(error, reconciliation));
        }
        finally
        {
            try { DisposeTransaction(transaction); }
            finally
            {
                try { TryDispose(root); }
                finally
                {
                    Zero(oldDescriptor);
                    Zero(descriptorBytes);
                    Zero(plaintext);
                    Zero(entropy);
                    Zero(encrypted);
                    Zero(encryptedHash);
                }
            }
        }
    }

    public Task DeleteAsync(string runId, CaptureGenerationId generation, string databaseTag,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var root = _files.VerifyRoot(_secretsDirectory, createIfMissing: false);
            var context = BuildContext(root.CanonicalRoot, runId, generation, databaseTag);
            _ = _files.Exists(root, context.SidecarRelativeName);
            Exception? failure = null;
            try { _credentials.DeleteGeneric(context.Target); }
            catch (Exception error) { failure = error; }
            try { _files.Delete(root, context.SidecarRelativeName); }
            catch (Exception error) { failure ??= error; }
            if (failure is not null)
                throw StoreFailure("cached_key_store_delete_failed", "缓存数据库密钥删除失败。", failure);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CachedKeyStoreException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw StoreFailure("cached_key_store_delete_failed", "缓存数据库密钥删除失败。", error);
        }
    }

    private void EnsureWindows()
    {
        if (!_isWindows())
            throw new PlatformNotSupportedException("仅支持 Windows 凭据管理器和 DPAPI CurrentUser。");
    }

    private Exception? ReconcileFailedSave(IWindowsAtomicSidecar? transaction, bool credentialWriteAttempted,
        string? credentialTarget, byte[]? oldDescriptor)
    {
        if (transaction is null) return null;
        Exception? failure = null;
        var diagnosticsBeforeRollback = transaction.Diagnostics.Count;
        try
        {
            transaction.Rollback();
        }
        catch (Exception error)
        {
            failure = error;
        }
        if (!transaction.RollbackSucceeded || transaction.Diagnostics.Count > diagnosticsBeforeRollback)
            ReportDiagnostic("缓存密钥文件事务回滚未完成。");
        if (!transaction.RollbackSucceeded)
            return failure ?? new InvalidOperationException("cached_key_sidecar_rollback_incomplete");

        if (credentialWriteAttempted && credentialTarget is not null)
        {
            try
            {
                if (oldDescriptor is null) _credentials.DeleteGeneric(credentialTarget);
                else _credentials.WriteGeneric(credentialTarget, oldDescriptor);
            }
            catch (Exception error)
            {
                ReportDiagnostic("缓存凭据恢复未完成。");
                return failure is null ? error : CombineInternal(failure, error);
            }
        }

        var diagnosticsBeforeConfirmation = transaction.Diagnostics.Count;
        try { transaction.ConfirmRollback(); }
        catch (Exception error) { failure = failure is null ? error : CombineInternal(failure, error); }
        if (transaction.Diagnostics.Count > diagnosticsBeforeConfirmation)
            ReportDiagnostic("缓存密钥文件事务清理未完成。");
        return failure;
    }

    private static void TryDispose(IDisposable? value)
    {
        try { value?.Dispose(); }
        catch (Exception) { }
    }

    private void DisposeTransaction(IWindowsAtomicSidecar? transaction)
    {
        if (transaction is null) return;
        try { transaction.Dispose(); }
        catch (Exception) { ReportDiagnostic("缓存密钥文件事务释放未完成。"); }
    }

    private void ReportDiagnostic(string message)
    {
        try { _diagnosticSink(message); }
        catch (Exception) { }
    }

    private static Exception CombineInternal(Exception first, Exception second) =>
        new AggregateException(first, second);

    private static CachedKeyStoreException StoreFailure(string code, string message, Exception cause) =>
        cause as CachedKeyStoreException ?? new CachedKeyStoreException(code, message, cause);

    private static void Zero(byte[]? value)
    {
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }

    private static bool DescriptorMatches(CredentialDescriptor? descriptor, CacheContext context) =>
        descriptor is not null && descriptor.Version == DescriptorVersion &&
        string.Equals(descriptor.RootIdentityHash, context.RootIdentityHash, StringComparison.Ordinal) &&
        string.Equals(descriptor.RunId, context.RunId, StringComparison.Ordinal) &&
        string.Equals(descriptor.GenerationHash, context.GenerationHash, StringComparison.Ordinal) &&
        string.Equals(descriptor.DatabaseTagHash, context.DatabaseTagHash, StringComparison.Ordinal) &&
        string.Equals(descriptor.SidecarRelativeName, context.SidecarRelativeName, StringComparison.Ordinal) &&
        IsSafeRelativeName(descriptor.SidecarRelativeName) && CachedKeyCandidate.IsSupportedLength(descriptor.KeyLength) &&
        IsLowerSha256(descriptor.SidecarSha256);

    private static CredentialDescriptor? DeserializeDescriptor(byte[] bytes) =>
        JsonSerializer.Deserialize<CredentialDescriptor>(bytes);

    private static CredentialDescriptor? TryDeserializeDescriptor(byte[]? bytes)
    {
        if (bytes is null) return null;
        try { return DeserializeDescriptor(bytes); }
        catch (JsonException) { return null; }
    }

    private static bool SidecarHashMatches(ReadOnlySpan<byte> value, string expectedSha256)
    {
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            expected = Convert.FromHexString(expectedSha256);
            actual = SHA256.HashData(value);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            Zero(expected);
            Zero(actual);
        }
    }

    private static bool IsLowerSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var item in value)
            if (item is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    private static CacheContext BuildContext(string canonicalRoot, string runId, CaptureGenerationId generation,
        string databaseTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var normalizedRoot = CanonicalizeRootForIdentity(canonicalRoot);
        var rootHash = CachedKeyIdentity.DatabaseTagHash(normalizedRoot);
        var runHash = CachedKeyIdentity.DatabaseTagHash(runId);
        var generationHash = CachedKeyIdentity.GenerationHash(generation);
        var databaseHash = CachedKeyIdentity.DatabaseTagHash(databaseTag);
        var sidecarBindingHash = CachedKeyIdentity.DatabaseTagHash($"{runHash}|{generationHash}|{databaseHash}");
        var relativeName = $"cached-key-{rootHash}-{sidecarBindingHash}.dpapi";
        var target = $"Deskmate.Footprint/CachedKey/{rootHash}/{runHash}/{generationHash}/{databaseHash}";
        return new CacheContext(normalizedRoot, rootHash, runId, generationHash, databaseHash, relativeName, target);
    }

    private static string CanonicalizeRootForIdentity(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) normalized = normalized.Replace('/', '\\').ToUpperInvariant();
        return normalized;
    }

    private static bool IsSafeRelativeName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) &&
        name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private sealed record CacheContext(string CanonicalRoot, string RootIdentityHash, string RunId,
        string GenerationHash, string DatabaseTagHash, string SidecarRelativeName, string Target);

    private sealed record CredentialDescriptor(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("root_identity_hash")] string RootIdentityHash,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("generation_hash")] string GenerationHash,
        [property: JsonPropertyName("database_tag_hash")] string DatabaseTagHash,
        [property: JsonPropertyName("sidecar_relative_name")] string SidecarRelativeName,
        [property: JsonPropertyName("key_length")] int KeyLength,
        [property: JsonPropertyName("sidecar_sha256")] string SidecarSha256);

    private sealed class ProtectedKeyDataApi : IWindowsDataProtectionApi
    {
        public byte[] Protect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> entropy) =>
            ProtectedKeyStore.Protect(value, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> entropy) =>
            ProtectedKeyStore.Unprotect(value, entropy);
    }
}

internal sealed class NativeWindowsKeyFileApi : IWindowsKeyFileApi
{
    internal const string PendingSuffix = ".pending";
    internal const string BackupSuffix = ".backup";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileDispositionInfo = 4;
    // FILE_RENAME_INFO (class 3) is supported on all target Windows versions.
    // FILE_RENAME_INFO_EX (class 22) is not available on older Windows builds and
    // returns ERROR_INVALID_PARAMETER (87) even when the buffer is otherwise valid.
    private const int FileRenameInfo = 3;

    public IWindowsRootLease VerifyRoot(string requestedRoot, bool createIfMissing)
    {
        var full = Path.GetFullPath(requestedRoot);
        EnsureNoReparseSegments(full);
        if (createIfMissing) Directory.CreateDirectory(full);
        EnsureNoReparseSegments(full);
        var handle = CreateFilePath(full, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (!createIfMissing && error is 2 or 3) return new MissingRootLease(full);
            throw new Win32Exception(error);
        }
        try
        {
            var info = FileInformation(handle);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new CachedKeyStoreException("cached_key_root_reparse", "缓存密钥目录不安全。");
            var final = FinalPath(handle);
            if (!PathsEqual(final, full))
                throw new CachedKeyStoreException("cached_key_root_identity_mismatch", "缓存密钥目录身份不匹配。");
            return new NativeRootLease(Path.GetFullPath(final), handle, FileIdentity.From(info));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Recover(IWindowsRootLease root, string relativeName, string expectedSha256)
    {
        var lease = RequireLease(root);
        lease.RequireExisting();
        if (!IsLowerSha256(expectedSha256))
            throw new CachedKeyStoreException("cached_key_sidecar_hash_mismatch", "缓存密钥文件校验失败。");
        var finalPath = ControlledPath(lease, relativeName);
        var tempPath = finalPath + PendingSuffix;
        var backupPath = finalPath + BackupSuffix;
        SafeFileHandle? final = null;
        SafeFileHandle? pending = null;
        SafeFileHandle? backup = null;
        try
        {
            const uint access = GenericRead | DeleteAccess;
            const uint share = FileShareRead | FileShareWrite;
            final = OpenVerifiedFileOrNull(lease, finalPath, access, share);
            pending = OpenVerifiedFileOrNull(lease, tempPath, access, share);
            backup = OpenVerifiedFileOrNull(lease, backupPath, access, share);

            var finalMatches = final is not null && HashMatches(lease, final, expectedSha256);
            var pendingMatches = pending is not null && HashMatches(lease, pending, expectedSha256);
            var backupMatches = backup is not null && HashMatches(lease, backup, expectedSha256);

            if (finalMatches)
            {
                DeleteAndClose(ref pending);
                DeleteAndClose(ref backup);
                lease.Validate();
                return;
            }

            SafeFileHandle winner;
            if (backupMatches) winner = backup!;
            else if (pendingMatches) winner = pending!;
            else
            {
                if (pending is null && backup is null)
                {
                    if (final is not null)
                        throw new CachedKeyStoreException("cached_key_sidecar_hash_mismatch",
                            "缓存密钥文件校验失败。");
                    return;
                }
                throw new CachedKeyStoreException("cached_key_sidecar_recovery_ambiguous",
                    "缓存密钥文件恢复状态不明确。");
            }

            DeleteAndClose(ref final);
            if (!ReferenceEquals(winner, pending)) DeleteAndClose(ref pending);
            if (!ReferenceEquals(winner, backup)) DeleteAndClose(ref backup);
            RenameByHandle(winner, lease.Handle, relativeName, replace: false);
            VerifyFileHandle(lease, finalPath, winner);
            lease.Validate();
        }
        finally
        {
            final?.Dispose();
            pending?.Dispose();
            backup?.Dispose();
        }
    }

    public bool Exists(IWindowsRootLease root, string relativeName)
    {
        var lease = RequireLease(root);
        lease.Validate();
        if (!lease.Exists) return false;
        var path = ControlledPath(lease, relativeName);
        using var handle = OpenVerifiedFileOrNull(lease, path, GenericRead);
        lease.Validate();
        return handle is not null;
    }

    public byte[] ReadAllBytes(IWindowsRootLease root, string relativeName)
    {
        var lease = RequireLease(root);
        lease.RequireExisting();
        var path = ControlledPath(lease, relativeName);
        using var handle = OpenVerifiedFile(lease, path, GenericRead);
        var length = RandomAccess.GetLength(handle);
        if (length > int.MaxValue) throw new IOException("Protected sidecar is too large.");
        var result = new byte[(int)length];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = RandomAccess.Read(handle, result.AsSpan(offset), offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        lease.Validate();
        return result;
    }

    public IWindowsAtomicSidecar BeginAtomicReplace(IWindowsRootLease root, string relativeName,
        ReadOnlySpan<byte> value)
    {
        var lease = RequireLease(root);
        lease.RequireExisting();
        var finalPath = ControlledPath(lease, relativeName);
        var tempPath = finalPath + PendingSuffix;
        var backupPath = finalPath + BackupSuffix;
        TryDeleteVerified(lease, tempPath, null);
        using (var unresolvedBackup = OpenVerifiedFileOrNull(lease, backupPath, GenericRead))
            if (unresolvedBackup is not null)
                throw new CachedKeyStoreException("cached_key_sidecar_recovery_ambiguous",
                    "缓存密钥文件恢复状态不明确。");
        SafeFileHandle? original = null;
        SafeFileHandle? pending = null;
        try
        {
            const uint share = FileShareRead | FileShareWrite;
            original = OpenVerifiedFileOrNull(lease, finalPath, GenericRead | DeleteAccess, share);
            pending = CreateFilePath(tempPath, GenericRead | GenericWrite | DeleteAccess, share, IntPtr.Zero, CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (pending.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            VerifyFileHandle(lease, tempPath, pending);
            RandomAccess.Write(pending, value, 0);
            if (!FlushFileBuffers(pending)) throw new Win32Exception(Marshal.GetLastWin32Error());
            VerifyFileHandle(lease, tempPath, pending);
            lease.Validate();
            var transaction = new NativeAtomicSidecar(lease, relativeName, finalPath, tempPath, backupPath,
                pending, original);
            pending = null;
            original = null;
            return transaction;
        }
        catch
        {
            original?.Dispose();
            if (pending is not null)
            {
                try
                {
                    if (!pending.IsInvalid) DeleteByHandle(pending);
                }
                finally { pending.Dispose(); }
            }
            else
            {
                TryDeleteVerified(lease, tempPath, null);
            }
            throw;
        }
    }

    public void Delete(IWindowsRootLease root, string relativeName)
    {
        var lease = RequireLease(root);
        lease.Validate();
        if (!lease.Exists) return;
        var finalPath = ControlledPath(lease, relativeName);
        var failures = new List<Exception>();
        TryDeleteVerified(lease, finalPath, failures);
        TryDeleteVerified(lease, finalPath + PendingSuffix, failures);
        TryDeleteVerified(lease, finalPath + BackupSuffix, failures);
        lease.Validate();
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static NativeRootLease RequireLease(IWindowsRootLease root)
    {
        if (root is not NativeRootLease lease)
            throw new CachedKeyStoreException("cached_key_root_identity_mismatch", "缓存密钥目录身份不匹配。");
        return lease;
    }

    private static string ControlledPath(NativeRootLease lease, string relativeName)
    {
        lease.Validate();
        if (string.IsNullOrWhiteSpace(relativeName) ||
            !string.Equals(relativeName, Path.GetFileName(relativeName), StringComparison.Ordinal))
            throw new CachedKeyStoreException("cached_key_sidecar_name_invalid", "缓存密钥文件名无效。");
        var root = Path.TrimEndingDirectorySeparator(lease.CanonicalRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativeName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CachedKeyStoreException("cached_key_sidecar_name_invalid", "缓存密钥文件名无效。");
        return path;
    }

    private static SafeFileHandle OpenVerifiedFile(NativeRootLease lease, string path, uint access)
    {
        return OpenVerifiedFileOrNull(lease, path, access) ?? throw new Win32Exception(2);
    }

    private static SafeFileHandle? OpenVerifiedFileOrNull(NativeRootLease lease, string path, uint access)
    {
        return OpenVerifiedFileOrNull(lease, path, access,
            FileShareRead | FileShareWrite | FileShareDelete);
    }

    private static SafeFileHandle? OpenVerifiedFileOrNull(NativeRootLease lease, string path, uint access,
        uint shareMode)
    {
        lease.RequireExisting();
        var handle = CreateFilePath(path, access, shareMode, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;
            throw new Win32Exception(error);
        }
        try
        {
            VerifyFileHandle(lease, path, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void VerifyFileHandle(NativeRootLease lease, string expectedPath, SafeFileHandle handle)
    {
        lease.Validate();
        var info = FileInformation(handle);
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new CachedKeyStoreException("cached_key_sidecar_reparse", "缓存密钥文件不安全。");
        var final = FinalPath(handle);
        if (!PathsEqual(final, expectedPath) || info.VolumeSerialNumber != lease.Identity.VolumeSerialNumber)
            throw new CachedKeyStoreException("cached_key_sidecar_identity_mismatch", "缓存密钥文件身份不匹配。");
        lease.Validate();
    }

    private static void DeleteVerified(NativeRootLease lease, string path)
    {
        using var handle = OpenVerifiedFileOrNull(lease, path, GenericRead | DeleteAccess);
        if (handle is null) return;
        DeleteByHandle(handle);
        lease.Validate();
    }

    private static void DeleteByHandle(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition,
                Marshal.SizeOf<FileDispositionInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void DeleteAndClose(ref SafeFileHandle? handle)
    {
        if (handle is null) return;
        var current = handle;
        handle = null;
        try { DeleteByHandle(current); }
        finally { current.Dispose(); }
    }

    private static void RenameByHandle(SafeFileHandle source, SafeFileHandle rootDirectory,
        string relativeTarget, bool replace)
    {
        if (string.IsNullOrWhiteSpace(relativeTarget) ||
            !string.Equals(relativeTarget, Path.GetFileName(relativeTarget), StringComparison.Ordinal))
            throw new CachedKeyStoreException("cached_key_sidecar_name_invalid", "缓存密钥文件名无效。");

        var fileName = Encoding.Unicode.GetBytes(relativeTarget);
        var rootOffset = Marshal.OffsetOf<FileRenameInformationBuffer>(
            nameof(FileRenameInformationBuffer.RootDirectory)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<FileRenameInformationBuffer>(
            nameof(FileRenameInformationBuffer.FileNameLength)).ToInt32();
        var nameOffset = Marshal.OffsetOf<FileRenameInformationBuffer>(
            nameof(FileRenameInformationBuffer.FileName)).ToInt32();
        var bufferSize = checked(nameOffset + fileName.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var rootReferenceAdded = false;
        try
        {
            for (var index = 0; index < bufferSize; index++) Marshal.WriteByte(buffer, index, 0);
            rootDirectory.DangerousAddRef(ref rootReferenceAdded);
            Marshal.WriteByte(buffer, 0, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, rootDirectory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, fileName.Length);
            Marshal.Copy(fileName, 0, IntPtr.Add(buffer, nameOffset), fileName.Length);
            if (!SetFileInformationByHandle(source, FileRenameInfo, buffer, bufferSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (rootReferenceAdded) rootDirectory.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
            CryptographicOperations.ZeroMemory(fileName);
        }
    }

    private static void TryDeleteVerified(NativeRootLease lease, string path, List<Exception>? failures)
    {
        try { DeleteVerified(lease, path); }
        catch (Exception error)
        {
            if (failures is null) throw;
            failures.Add(error);
        }
    }

    private static void EnsureNoReparseSegments(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Path root is missing.");
        var current = root;
        var remainder = full[root.Length..];
        foreach (var segment in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            using var handle = CreateFilePath(current, GenericRead, FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) continue;
                throw new Win32Exception(error);
            }
            if ((FileInformation(handle).FileAttributes & FileAttributeReparsePoint) != 0)
                throw new CachedKeyStoreException("cached_key_root_reparse", "缓存密钥目录不安全。");
        }
    }

    private static bool HashMatches(NativeRootLease lease, SafeFileHandle handle, string expectedSha256)
    {
        byte[]? expected = null;
        byte[]? actual = null;
        var buffer = new byte[64 * 1024];
        try
        {
            expected = Convert.FromHexString(expectedSha256);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (true)
            {
                var read = RandomAccess.Read(handle, buffer, offset);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            actual = hash.GetHashAndReset();
            lease.Validate();
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static bool IsLowerSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var item in value)
            if (item is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    private static ByHandleFileInformation FileInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return information;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < buffer.Capacity) return NormalizeDevicePath(buffer.ToString());
            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeDevicePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return path[4..];
        return path;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private sealed class MissingRootLease(string canonicalRoot) : NativeRootLease(canonicalRoot, null, default)
    {
    }

    private class NativeRootLease : IWindowsRootLease
    {
        private readonly SafeFileHandle? _handle;
        private bool _disposed;

        public NativeRootLease(string canonicalRoot, SafeFileHandle? handle, FileIdentity identity)
        {
            CanonicalRoot = Path.GetFullPath(canonicalRoot);
            _handle = handle;
            Identity = identity;
        }

        public string CanonicalRoot { get; }
        public FileIdentity Identity { get; }
        public bool Exists => _handle is not null;
        public SafeFileHandle Handle
        {
            get
            {
                RequireExisting();
                return _handle!;
            }
        }

        public void RequireExisting()
        {
            Validate();
            if (!Exists)
                throw new CachedKeyStoreException("cached_key_root_identity_mismatch", "缓存密钥目录身份不匹配。");
        }

        public void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is null)
            {
                using var unexpected = CreateFilePath(CanonicalRoot, GenericRead,
                    FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                if (unexpected.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is 2 or 3) return;
                    throw new Win32Exception(error);
                }
                throw new CachedKeyStoreException("cached_key_root_identity_mismatch", "缓存密钥目录身份不匹配。");
            }
            var info = FileInformation(_handle);
            if (!FileIdentity.From(info).Equals(Identity) ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
                !PathsEqual(FinalPath(_handle), CanonicalRoot))
                throw new CachedKeyStoreException("cached_key_root_identity_mismatch", "缓存密钥目录身份不匹配。");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _handle?.Dispose();
            _disposed = true;
        }
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow)
    {
        public static FileIdentity From(ByHandleFileInformation value) =>
            new(value.VolumeSerialNumber, value.FileIndexHigh, value.FileIndexLow);
    }

    private sealed class NativeAtomicSidecar : IWindowsAtomicSidecar
    {
        private enum TransactionState
        {
            Prepared,
            CommitAttempted,
            Committed,
            Completed,
            RollbackIncomplete,
            RolledBack,
            RollbackConfirmed
        }

        private readonly NativeRootLease _root;
        private readonly string _relativeName;
        private readonly string _final;
        private readonly string _temp;
        private readonly string _backup;
        private readonly SafeFileHandle _pendingHandle;
        private readonly SafeFileHandle? _originalHandle;
        private readonly List<Exception> _diagnostics = [];
        private TransactionState _state = TransactionState.Prepared;
        private bool _originalMoved;
        private bool _replacementApplied;
        private bool _disposed;

        public NativeAtomicSidecar(NativeRootLease root, string relativeName, string final, string temp,
            string backup, SafeFileHandle pendingHandle, SafeFileHandle? originalHandle)
        {
            _root = root;
            _relativeName = relativeName;
            _final = final;
            _temp = temp;
            _backup = backup;
            _pendingHandle = pendingHandle;
            _originalHandle = originalHandle;
        }

        public IReadOnlyList<Exception> Diagnostics => _diagnostics;
        public bool RollbackSucceeded => _state is TransactionState.RolledBack or TransactionState.RollbackConfirmed;

        public void Commit()
        {
            if (_state != TransactionState.Prepared) throw new InvalidOperationException();
            _state = TransactionState.CommitAttempted;
            try
            {
                _root.Validate();
                VerifyFileHandle(_root, _temp, _pendingHandle);
                if (_originalHandle is not null)
                {
                    VerifyFileHandle(_root, _final, _originalHandle);
                    RenameByHandle(_originalHandle, _root.Handle, _relativeName + BackupSuffix, replace: false);
                    _originalMoved = true;
                    VerifyFileHandle(_root, _backup, _originalHandle);
                }
                else
                {
                    using (var unexpected = OpenVerifiedFileOrNull(_root, _final, GenericRead))
                        if (unexpected is not null)
                            throw new CachedKeyStoreException("cached_key_sidecar_identity_mismatch",
                                "缓存密钥文件身份不匹配。");
                }
                RenameByHandle(_pendingHandle, _root.Handle, _relativeName, replace: false);
                _replacementApplied = true;
                VerifyFileHandle(_root, _final, _pendingHandle);
                _root.Validate();
                _state = TransactionState.Committed;
            }
            catch (Exception error)
            {
                _diagnostics.Add(error);
                throw;
            }
        }

        public void Complete()
        {
            if (_state is TransactionState.Completed or TransactionState.RolledBack or
                TransactionState.RollbackConfirmed) return;
            if (_state != TransactionState.Committed) throw new InvalidOperationException();
            if (_originalHandle is not null)
            {
                try
                {
                    VerifyFileHandle(_root, _backup, _originalHandle);
                    DeleteByHandle(_originalHandle);
                }
                catch (Exception error) { _diagnostics.Add(error); }
            }
            _state = TransactionState.Completed;
        }

        public void Rollback()
        {
            if (_state is TransactionState.Completed or TransactionState.RolledBack or
                TransactionState.RollbackConfirmed or TransactionState.RollbackIncomplete) return;
            var failed = false;
            if (_replacementApplied)
            {
                try
                {
                    VerifyFileHandle(_root, _final, _pendingHandle);
                    RenameByHandle(_pendingHandle, _root.Handle, _relativeName + PendingSuffix, replace: false);
                    _replacementApplied = false;
                    VerifyFileHandle(_root, _temp, _pendingHandle);
                }
                catch (Exception error)
                {
                    _diagnostics.Add(error);
                    failed = true;
                }
            }

            if (_originalMoved && !_replacementApplied && _originalHandle is not null)
            {
                try
                {
                    VerifyFileHandle(_root, _backup, _originalHandle);
                    RenameByHandle(_originalHandle, _root.Handle, _relativeName, replace: false);
                    _originalMoved = false;
                    VerifyFileHandle(_root, _final, _originalHandle);
                }
                catch (Exception error)
                {
                    _diagnostics.Add(error);
                    failed = true;
                }
            }

            _root.Validate();
            _state = failed ? TransactionState.RollbackIncomplete : TransactionState.RolledBack;
        }

        public void ConfirmRollback()
        {
            if (_state == TransactionState.RollbackConfirmed) return;
            if (_state != TransactionState.RolledBack) throw new InvalidOperationException();
            try
            {
                VerifyFileHandle(_root, _temp, _pendingHandle);
                DeleteByHandle(_pendingHandle);
                _state = TransactionState.RollbackConfirmed;
            }
            catch (Exception error) { _diagnostics.Add(error); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _pendingHandle.Dispose();
            _originalHandle?.Dispose();
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationBuffer
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    private static SafeFileHandle CreateFilePath(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile) =>
        CreateFileW(ToExtendedLengthPath(fileName), desiredAccess, shareMode, securityAttributes,
            creationDisposition, flagsAndAttributes, templateFile);

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
            path[2] is '\\' or '/')
            return @"\\?\" + path.Replace('/', '\\');
        return path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath,
        uint filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInformation fileInformation, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        IntPtr fileInformation, int bufferSize);
}

internal sealed class NativeWindowsCredentialApi : IWindowsCredentialApi
{
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public byte[]? ReadGeneric(string target)
    {
        if (!CredRead(target, WindowsCredentialKeyStore.GenericCredentialType, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var result = new byte[credential.CredentialBlobSize];
            if (result.Length > 0) Marshal.Copy(credential.CredentialBlob, result, 0, result.Length);
            return result;
        }
        finally { CredFree(pointer); }
    }

    public void WriteGeneric(string target, ReadOnlySpan<byte> descriptor)
    {
        var copy = descriptor.ToArray();
        var blob = IntPtr.Zero;
        try
        {
            if (copy.Length > 0)
            {
                blob = Marshal.AllocHGlobal(copy.Length);
                Marshal.Copy(copy, 0, blob, copy.Length);
            }
            var credential = new NativeCredential
            {
                Type = WindowsCredentialKeyStore.GenericCredentialType,
                TargetName = target,
                CredentialBlobSize = copy.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < descriptor.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }
    }

    public void DeleteGeneric(string target)
    {
        if (CredDelete(target, WindowsCredentialKeyStore.GenericCredentialType, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
