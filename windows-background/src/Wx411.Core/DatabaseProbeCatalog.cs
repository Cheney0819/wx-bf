using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record DatabaseFileGeneration(
    long Length,
    DateTime LastWriteTimeUtc,
    string FileIdentity,
    string WalFingerprint = "missing",
    string SharedMemoryFingerprint = "missing");

public sealed class DatabaseSamplePage : IDisposable
{
    private byte[]? _data;

    internal DatabaseSamplePage(int pageSize, int pageNumber, byte[] data)
    {
        PageSize = pageSize;
        PageNumber = pageNumber;
        _data = data;
    }

    public int PageSize { get; }
    public int PageNumber { get; }
    public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(DatabaseSamplePage));

    public void Dispose()
    {
        var data = Interlocked.Exchange(ref _data, null);
        if (data is not null) CryptographicOperations.ZeroMemory(data);
    }
}

public sealed class DatabaseProbeDescriptor : IDisposable
{
    private const int StableWalPrefixLength = 64 * 1024;
    private const int MaxReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private byte[]? _salt;
    private bool _disposed;

    private DatabaseProbeDescriptor(
        string path,
        DatabaseFileGeneration generation,
        byte[] salt,
        IReadOnlyList<CipherProfile> profiles,
        IReadOnlyList<DatabaseSamplePage> samplePages)
    {
        Path = path;
        Generation = generation;
        _salt = salt;
        Profiles = profiles;
        SamplePages = samplePages;
    }

    public string Path { get; }
    public DatabaseFileGeneration Generation { get; }
    public long Length => Generation.Length;
    public byte[] Salt => _salt ?? throw new ObjectDisposedException(nameof(DatabaseProbeDescriptor));
    public IReadOnlyList<CipherProfile> Profiles { get; }
    public IReadOnlyList<DatabaseSamplePage> SamplePages { get; }

    public static DatabaseProbeDescriptor Read(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = System.IO.Path.GetFullPath(path);
        Exception? lastError = null;
        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadOnce(normalizedPath, cancellationToken);
            }
            catch (DatabaseGenerationChangedException ex)
            {
                lastError = ex;
                if (attempt + 1 < MaxReadAttempts &&
                    cancellationToken.WaitHandle.WaitOne(RetryDelay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (EndOfStreamException ex)
            {
                lastError = new DatabaseGenerationChangedException(normalizedPath, ex.Message);
                if (attempt + 1 < MaxReadAttempts &&
                    cancellationToken.WaitHandle.WaitOne(RetryDelay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        throw lastError ?? new IOException("Database descriptor could not be read consistently.");
    }

    internal static DatabaseFileGeneration GetGeneration(string path)
    {
        var normalizedPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(normalizedPath);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Database file was not found.", normalizedPath);
        var salt = new byte[16];
        try
        {
            using var stream = OpenShared(normalizedPath);
            ReadExactly(stream, salt, CancellationToken.None);
            var identity = $"{info.CreationTimeUtc.Ticks:X16}:{Convert.ToHexString(SHA256.HashData(salt))}";
            var wal = ReadSidecarFingerprint(normalizedPath + "-wal", includePrefix: true);
            var shm = ReadSidecarFingerprint(normalizedPath + "-shm", includePrefix: false);
            return new DatabaseFileGeneration(
                stream.Length,
                info.LastWriteTimeUtc,
                identity,
                wal,
                shm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static string ReadSidecarFingerprint(string path, bool includePrefix)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) return "missing";

        var length = info.Length;
        var lastWrite = info.LastWriteTimeUtc;
        var prefixLength = includePrefix
            ? checked((int)Math.Min(length, StableWalPrefixLength))
            : 0;
        var prefix = new byte[prefixLength];
        try
        {
            if (prefixLength > 0)
            {
                using var stream = OpenShared(path);
                ReadExactly(stream, prefix, CancellationToken.None);
            }

            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != lastWrite)
                throw new DatabaseGenerationChangedException(path, "sidecar changed during fingerprinting");

            var digest = SHA256.HashData(prefix);
            try
            {
                return $"{length}:{lastWrite.Ticks}:{Convert.ToHexString(digest)}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
        }
    }

    internal DatabaseSamplePage RequiredSample(int pageSize, int pageNumber) =>
        SamplePages.First(page => page.PageSize == pageSize && page.PageNumber == pageNumber);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var page in SamplePages) page.Dispose();
        var salt = Interlocked.Exchange(ref _salt, null);
        if (salt is not null) CryptographicOperations.ZeroMemory(salt);
    }

    private static DatabaseProbeDescriptor ReadOnce(
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        var before = GetGeneration(normalizedPath);
        var salt = new byte[16];
        var samples = new List<DatabaseSamplePage>();
        var completed = false;
        try
        {
            using var stream = OpenShared(normalizedPath);
            if (stream.Length != before.Length)
                throw new DatabaseGenerationChangedException(normalizedPath);
            ReadExactly(stream, salt, cancellationToken);
            var profiles = CipherProfileProbe.CandidateProfilesFor(stream.Length);
            foreach (var pageSize in profiles.Select(profile => profile.PageSize).Distinct())
            {
                var pageCount = checked((int)(stream.Length / pageSize));
                foreach (var pageNumber in CipherProfileProbe.SelectSamplePages(pageCount))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Position = checked((long)(pageNumber - 1) * pageSize);
                    var data = new byte[pageSize];
                    try
                    {
                        ReadExactly(stream, data, cancellationToken);
                        samples.Add(new DatabaseSamplePage(pageSize, pageNumber, data));
                    }
                    catch
                    {
                        CryptographicOperations.ZeroMemory(data);
                        throw;
                    }
                }
            }

            var after = GetGeneration(normalizedPath);
            if (before != after)
                throw new DatabaseGenerationChangedException(normalizedPath);
            completed = true;
            return new DatabaseProbeDescriptor(
                normalizedPath,
                before,
                salt,
                profiles,
                Array.AsReadOnly(samples.ToArray()));
        }
        finally
        {
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(salt);
                foreach (var sample in samples) sample.Dispose();
            }
        }
    }

    private static FileStream OpenShared(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.RandomAccess);

    private static void ReadExactly(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new EndOfStreamException("Database changed during descriptor read.");
            offset += read;
        }
    }

    private sealed class DatabaseGenerationChangedException : IOException
    {
        internal DatabaseGenerationChangedException(string path, string? detail = null)
            : base(detail is null
                ? $"Database generation changed while reading descriptor: {path}"
                : $"Database generation changed while reading descriptor: {path}; {detail}")
        {
        }
    }
}

public sealed record DatabaseProbeCatalogUpdate(
    IReadOnlyList<string> AddedPaths,
    IReadOnlyList<string> ReplacedPaths,
    IReadOnlyList<string> UnavailablePaths);

public sealed class DatabaseProbeCatalog : IDisposable
{
    private readonly string _selectedPath;
    private readonly Dictionary<string, DatabaseProbeDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _skipped = [];
    private bool _disposed;

    private DatabaseProbeCatalog(string selectedPath)
    {
        _selectedPath = selectedPath;
    }

    public IReadOnlyList<DatabaseProbeDescriptor> Descriptors =>
        Array.AsReadOnly(_descriptors.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray());

    public IReadOnlyList<string> SkippedPaths => Array.AsReadOnly(_skipped.ToArray());

    public static DatabaseProbeCatalog Create(
        string selectedPath,
        IEnumerable<string> discoveredPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ArgumentNullException.ThrowIfNull(discoveredPaths);
        var selected = Path.GetFullPath(selectedPath);
        var catalog = new DatabaseProbeCatalog(selected);
        try
        {
            foreach (var path in discoveredPaths.Append(selected)
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                catalog._knownPaths.Add(path);
                try
                {
                    catalog._descriptors.Add(path, DatabaseProbeDescriptor.Read(path, cancellationToken));
                }
                catch (Exception ex) when (!string.Equals(path, selected, StringComparison.OrdinalIgnoreCase) &&
                                           ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    catalog._skipped.Add(path);
                }
            }
            return catalog;
        }
        catch
        {
            catalog.Dispose();
            throw;
        }
    }

    public void MarkConfirmed(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _confirmed.Add(Path.GetFullPath(path));
    }

    public Task<DatabaseProbeCatalogUpdate> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var added = new List<string>();
        var replaced = new List<string>();
        var unavailable = new List<string>();
        var directories = _knownPaths
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory!, "*.db", SearchOption.TopDirectoryOnly))
                _knownPaths.Add(Path.GetFullPath(path));
        }

        foreach (var path in _knownPaths.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                unavailable.Add(path);
                continue;
            }

            if (!_descriptors.TryGetValue(path, out var existing))
            {
                try
                {
                    _descriptors.Add(path, DatabaseProbeDescriptor.Read(path, cancellationToken));
                    added.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    unavailable.Add(path);
                }
                continue;
            }

            if (_confirmed.Contains(path)) continue;
            try
            {
                if (DatabaseProbeDescriptor.GetGeneration(path) == existing.Generation) continue;
                var replacement = DatabaseProbeDescriptor.Read(path, cancellationToken);
                _descriptors[path] = replacement;
                existing.Dispose();
                replaced.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                unavailable.Add(path);
            }
        }

        return Task.FromResult(new DatabaseProbeCatalogUpdate(
            Array.AsReadOnly(added.ToArray()),
            Array.AsReadOnly(replaced.ToArray()),
            Array.AsReadOnly(unavailable.ToArray())));
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var descriptor in _descriptors.Values) descriptor.Dispose();
        _descriptors.Clear();
        _disposed = true;
    }
}
