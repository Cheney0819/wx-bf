using System.Security.Cryptography;

namespace Footprint.Core;

public sealed record StableFileSnapshot(
    string DestinationPath,
    long Size,
    DateTimeOffset LastWriteUtc,
    string Sha256,
    int StabilityAttempts,
    bool Stable);

public sealed class SnapshotRecoveryRequiredException : IOException
{
    internal SnapshotRecoveryRequiredException(string destinationDirectory, string recoveryDirectory,
        string diagnosticPath, int stabilityAttempt)
        : base("数据库快照组回滚未完成，需要使用保留的恢复日志进行恢复。")
    {
        DestinationDirectory = destinationDirectory;
        RecoveryDirectory = recoveryDirectory;
        DiagnosticPath = diagnosticPath;
        StabilityAttempt = stabilityAttempt;
    }

    public string DestinationDirectory { get; }
    public string RecoveryDirectory { get; }
    public string DiagnosticPath { get; }
    public int StabilityAttempt { get; }
    public bool RecoveryRequired => true;
    public string ErrorCode => "snapshot_recovery_required";
}

public static class StableSnapshotter
{
    public static async Task<SnapshotManifest> CreateAsync(string databasePath, string destinationDirectory,
        int maxAttempts, CancellationToken cancellationToken, Func<int, Task>? afterCopyAttempt = null)
    {
        return await CreateAsync(databasePath, destinationDirectory, maxAttempts, cancellationToken,
            afterCopyAttempt, afterPromote: null, requiredStableObservations: 1);
    }

    public static async Task<SnapshotManifest> CreateCoherentAsync(string databasePath, string destinationDirectory,
        int maxAttempts, CancellationToken cancellationToken, Func<int, Task>? afterCopyAttempt = null)
    {
        return await CreateAsync(databasePath, destinationDirectory, maxAttempts, cancellationToken,
            afterCopyAttempt, afterPromote: null, requiredStableObservations: 2);
    }

    internal static async Task<SnapshotManifest> CreateAsync(string databasePath, string destinationDirectory,
        int maxAttempts, CancellationToken cancellationToken, Func<int, Task>? afterCopyAttempt,
        Action<int>? afterPromote, Action<string>? beforeRollbackDelete = null, int requiredStableObservations = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("Database file does not exist.", databasePath);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        if (requiredStableObservations < 1) throw new ArgumentOutOfRangeException(nameof(requiredStableObservations));
        Directory.CreateDirectory(destinationDirectory);
        var consecutiveStableObservations = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptDirectory = Path.Combine(destinationDirectory, $".snapshot-attempt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(attemptDirectory);
            var preserveRecoveryDirectory = false;
            try
            {
                var sources = ExistingSources(databasePath);
                var before = await ReadStatesAsync(sources, cancellationToken);
                foreach (var source in sources)
                    await CopyFileAsync(source, Path.Combine(attemptDirectory, Path.GetFileName(source)), cancellationToken);

                if (afterCopyAttempt is not null) await afterCopyAttempt(attempt);

                var afterSources = ExistingSources(databasePath);
                var after = await ReadStatesAsync(afterSources, cancellationToken);
                if (!sources.SequenceEqual(afterSources, PathComparer()) || !before.SequenceEqual(after) ||
                    !await CopiesMatchAsync(attemptDirectory, before, cancellationToken))
                {
                    consecutiveStableObservations = 0;
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
                    continue;
                }
                consecutiveStableObservations++;
                if (consecutiveStableObservations < requiredStableObservations)
                {
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
                    continue;
                }

                PromoteGroup(attemptDirectory, destinationDirectory, databasePath, sources, attempt, afterPromote,
                    beforeRollbackDelete);

                return new SnapshotManifest
                {
                    Directory = destinationDirectory,
                    Stable = true,
                    StabilityAttempts = attempt,
                    Files = before.Select(state => new SnapshotFile(Path.GetFileName(state.Path), state.Size,
                        state.LastWriteUtc, state.Sha256)).ToList()
                };
            }
            catch (SnapshotRecoveryRequiredException)
            {
                preserveRecoveryDirectory = true;
                throw;
            }
            catch (Exception exception) when (IsRetryableSnapshotFailure(exception))
            {
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
            finally
            {
                if (!preserveRecoveryDirectory) DeleteDirectory(attemptDirectory);
            }
        }

        return new SnapshotManifest
        {
            Directory = destinationDirectory,
            Stable = false,
            StabilityAttempts = maxAttempts
        };
    }

    public static async Task<StableFileSnapshot> CreateFileAsync(string sourcePath, string destinationPath,
        int maxAttempts, CancellationToken cancellationToken, Func<int, Task>? afterCopyAttempt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Snapshot source does not exist.", sourcePath);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = destinationPath + $".partial-{Guid.NewGuid():N}";
            try
            {
                var before = await ReadStateAsync(sourcePath, cancellationToken);
                await CopyFileAsync(sourcePath, temporary, cancellationToken);
                if (afterCopyAttempt is not null) await afterCopyAttempt(attempt);
                if (!File.Exists(sourcePath)) continue;
                var after = await ReadStateAsync(sourcePath, cancellationToken);
                var copied = await ReadStateAsync(temporary, cancellationToken);
                if (before != after || copied.Size != before.Size ||
                    !string.Equals(copied.Sha256, before.Sha256, StringComparison.Ordinal))
                {
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
                    continue;
                }

                AtomicFile.Replace(temporary, destinationPath);
                return new StableFileSnapshot(destinationPath, before.Size, before.LastWriteUtc, before.Sha256,
                    attempt, true);
            }
            catch (Exception exception) when (IsRetryableSnapshotFailure(exception))
            {
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
            finally
            {
                DeleteFile(temporary);
            }
        }

        return new StableFileSnapshot(destinationPath, 0, default, string.Empty, maxAttempts, false);
    }

    private static string[] ExistingSources(string databasePath) =>
        new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists).ToArray();

    private static async Task<FileState[]> ReadStatesAsync(IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new List<FileState>();
        foreach (var path in paths)
            result.Add(await ReadStateAsync(path, cancellationToken));
        return result.ToArray();
    }

    private static async Task<FileState> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var lastWriteUtc = info.LastWriteTimeUtc;
        var sha256 = await HashFileSharedAsync(path, cancellationToken);
        info.Refresh();
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != lastWriteUtc)
            return new FileState(path, info.Exists ? info.Length : -1, info.LastWriteTimeUtc, sha256 + ":changed");
        return new FileState(path, size, lastWriteUtc, sha256);
    }

    private static async Task<bool> CopiesMatchAsync(string attemptDirectory, IEnumerable<FileState> states,
        CancellationToken cancellationToken)
    {
        foreach (var state in states)
        {
            var path = Path.Combine(attemptDirectory, Path.GetFileName(state.Path));
            if (!File.Exists(path)) return false;
            var copied = await ReadStateAsync(path, cancellationToken);
            if (copied.Size != state.Size || !string.Equals(copied.Sha256, state.Sha256, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void PromoteGroup(string attemptDirectory, string destinationDirectory, string databasePath,
        IReadOnlyCollection<string> sources, int stabilityAttempt, Action<int>? afterPromote,
        Action<string>? beforeRollbackDelete)
    {
        var names = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Select(path => Path.GetFileName(path)!).ToArray();
        var sourceNames = sources.Select(path => Path.GetFileName(path)!).ToHashSet(PathComparer());
        var rollbackDirectory = Path.Combine(attemptDirectory, ".rollback");
        Directory.CreateDirectory(rollbackDirectory);
        var backedUp = new List<(string Backup, string Target)>();
        var promoted = new List<string>();
        try
        {
            foreach (var name in names)
            {
                var target = Path.Combine(destinationDirectory, name);
                if (!File.Exists(target)) continue;
                var backup = Path.Combine(rollbackDirectory, name);
                File.Move(target, backup);
                backedUp.Add((backup, target));
            }

            var promotedCount = 0;
            foreach (var name in names.Where(sourceNames.Contains))
            {
                var temporary = Path.Combine(attemptDirectory, name);
                var target = Path.Combine(destinationDirectory, name);
                File.Move(temporary, target);
                promoted.Add(target);
                afterPromote?.Invoke(++promotedCount);
            }
        }
        catch (Exception)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var target in promoted.AsEnumerable().Reverse())
            {
                try
                {
                    DeleteForRollback(target, beforeRollbackDelete);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(exception);
                }
            }

            foreach (var (backup, target) in backedUp.AsEnumerable().Reverse())
            {
                try
                {
                    DeleteForRollback(target, beforeRollbackDelete);
                    if (File.Exists(backup)) File.Move(backup, target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(exception);
                }
            }

            if (rollbackFailures.Count > 0)
            {
                var diagnosticPath = WriteRecoveryDiagnostic(attemptDirectory, destinationDirectory,
                    rollbackDirectory, names);
                throw new SnapshotRecoveryRequiredException(destinationDirectory, attemptDirectory, diagnosticPath,
                    stabilityAttempt);
            }
            throw;
        }
    }

    private static void DeleteForRollback(string path, Action<string>? beforeRollbackDelete)
    {
        if (!File.Exists(path)) return;
        beforeRollbackDelete?.Invoke(path);
        File.Delete(path);
    }

    private static string WriteRecoveryDiagnostic(string attemptDirectory, string destinationDirectory,
        string rollbackDirectory, IEnumerable<string> names)
    {
        var diagnosticPath = Path.Combine(attemptDirectory, "RECOVERY_REQUIRED.txt");
        var lines = new List<string>
        {
            "状态：数据库快照组回滚未完成，需要恢复。",
            $"目标目录：{destinationDirectory}",
            $"恢复日志目录：{attemptDirectory}"
        };
        foreach (var name in names)
        {
            lines.Add($"目标文件 {name}：{Existence(Path.Combine(destinationDirectory, name))}");
            lines.Add($"备份文件 {name}：{Existence(Path.Combine(rollbackDirectory, name))}");
            lines.Add($"待发布文件 {name}：{Existence(Path.Combine(attemptDirectory, name))}");
        }

        try
        {
            using var stream = new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush();
            }
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return diagnosticPath;
    }

    private static string Existence(string path) => File.Exists(path) ? "存在" : "不存在";

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsRetryableSnapshotFailure(Exception exception) =>
        exception is not SnapshotRecoveryRequiredException && exception is IOException or UnauthorizedAccessException;

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record FileState(string Path, long Size, DateTimeOffset LastWriteUtc, string Sha256);
}
