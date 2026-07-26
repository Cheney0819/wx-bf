using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record DatabaseCaptureMatch(
    string DatabaseId,
    CipherProfileMatch ProfileMatch,
    string Representation,
    string CallpointName);

public sealed record CaptureCollectionUpdate(
    IReadOnlyList<DatabaseCaptureMatch> NewMatches,
    bool IsComplete);

public sealed class DatabaseCaptureTarget : IDisposable
{
    private DatabaseProbeDescriptor _descriptor;
    private readonly List<PendingCapture> _pending = [];
    private bool _disposed;

    public DatabaseCaptureTarget(DatabaseProbeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptor = descriptor;
    }

    public string DatabaseId => _descriptor.Path;
    public DatabaseProbeDescriptor Descriptor => _descriptor;
    public DatabaseCaptureMatch? Match { get; private set; }
    internal bool IsMatched => Match is not null;
    internal bool HasPending => _pending.Count > 0;
    internal IReadOnlyList<DatabaseCaptureMatch> PendingMatches =>
        Array.AsReadOnly(_pending.Select(item => item.Match).ToArray());

    internal IReadOnlyList<NormalizedCapturedKey> Normalize(byte[] captured)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CapturedKeyNormalizer.Normalize(captured, _descriptor.Salt);
    }

    internal CipherProfileMatch? FindMatch(
        ReadOnlySpan<byte> rawKey,
        CipherProfileProbeCounters counters,
        CancellationToken cancellationToken) =>
        CipherProfileProbe.FindMatch(_descriptor, rawKey, cancellationToken, counters);

    internal bool HasPendingKey(ReadOnlySpan<byte> rawKey)
    {
        foreach (var pending in _pending)
        {
            if (CryptographicOperations.FixedTimeEquals(pending.RawKey, rawKey)) return true;
        }
        return false;
    }

    internal DatabaseCaptureMatch RecordPending(
        ReadOnlySpan<byte> rawKey,
        CipherProfileMatch profileMatch,
        string representation,
        string callpointName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var match = new DatabaseCaptureMatch(
            DatabaseId,
            profileMatch,
            representation,
            callpointName);
        _pending.Add(new PendingCapture(match, rawKey.ToArray()));
        return match;
    }

    internal byte[] CopyPendingKey(DatabaseCaptureMatch match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pending = _pending.SingleOrDefault(item => ReferenceEquals(item.Match, match)) ??
            throw new ArgumentException("Match is not pending for this target.", nameof(match));
        return pending.RawKey.ToArray();
    }

    internal void ConfirmExport(DatabaseCaptureMatch match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending.Any(item => ReferenceEquals(item.Match, match)))
            throw new ArgumentException("Match is not pending for this target.", nameof(match));
        Match = match;
        ClearPending();
    }

    internal void SynchronizeDescriptor(
        DatabaseProbeDescriptor descriptor,
        CipherProfileProbeCounters counters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_descriptor.Generation == descriptor.Generation) return;
        _descriptor = descriptor;
        if (IsMatched) return;

        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = _pending[index];
            var profileMatch = FindMatch(pending.RawKey, counters, cancellationToken);
            if (profileMatch is null)
            {
                pending.Dispose();
                _pending.RemoveAt(index);
                continue;
            }
            pending.Match = pending.Match with
            {
                DatabaseId = descriptor.Path,
                ProfileMatch = profileMatch,
            };
        }
    }

    internal void ReleaseSensitiveBuffers() => ClearPending();

    public void Dispose()
    {
        if (_disposed) return;
        ClearPending();
        Match = null;
        _disposed = true;
    }

    private void ClearPending()
    {
        foreach (var pending in _pending) pending.Dispose();
        _pending.Clear();
    }

    private sealed class PendingCapture : IDisposable
    {
        private byte[]? _rawKey;

        internal PendingCapture(DatabaseCaptureMatch match, byte[] rawKey)
        {
            Match = match;
            _rawKey = rawKey;
        }

        internal DatabaseCaptureMatch Match { get; set; }
        internal ReadOnlySpan<byte> RawKey =>
            _rawKey ?? throw new ObjectDisposedException(nameof(PendingCapture));

        public void Dispose()
        {
            var rawKey = Interlocked.Exchange(ref _rawKey, null);
            if (rawKey is not null) CryptographicOperations.ZeroMemory(rawKey);
        }
    }
}

public sealed class MultiDatabaseCaptureCollector : IDisposable
{
    private readonly Dictionary<string, DatabaseCaptureTarget> _targets =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MultiDatabaseCaptureCollector(IEnumerable<DatabaseProbeDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        Synchronize(descriptors, new CipherProfileProbeCounters());
        if (_targets.Count == 0)
            throw new ArgumentException("At least one database descriptor is required.", nameof(descriptors));
    }

    public bool IsComplete => _targets.Values.All(target => target.IsMatched);
    public bool IsReadyForValidation =>
        _targets.Values.All(target => target.IsMatched || target.HasPending);
    public IReadOnlyList<DatabaseCaptureMatch> Matches => Array.AsReadOnly(
        _targets.Values.Where(target => target.Match is not null).Select(target => target.Match!).ToArray());
    public IReadOnlyList<DatabaseCaptureMatch> PendingMatches => Array.AsReadOnly(
        _targets.Values.SelectMany(target => target.PendingMatches).ToArray());
    public IReadOnlyList<string> UnmatchedIds => Array.AsReadOnly(
        _targets.Values.Where(target => !target.IsMatched && !target.HasPending)
            .Select(target => target.DatabaseId).ToArray());
    public IReadOnlyList<string> PendingDatabaseIds => Array.AsReadOnly(
        _targets.Values.Where(target => !target.IsMatched && target.HasPending)
            .Select(target => target.DatabaseId).ToArray());

    public void Synchronize(
        IEnumerable<DatabaseProbeDescriptor> descriptors,
        CipherProfileProbeCounters counters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(counters);
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_targets.TryGetValue(descriptor.Path, out var target))
                target.SynchronizeDescriptor(descriptor, counters, cancellationToken);
            else
                _targets.Add(descriptor.Path, new DatabaseCaptureTarget(descriptor));
        }
    }

    public CaptureCollectionUpdate TryCollect(
        CapturedKeyMaterial captured,
        CipherProfileProbeCounters counters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(counters);
        cancellationToken.ThrowIfCancellationRequested();
        if (captured.KeyData is not { Length: > 0 } capturedBytes)
            return new CaptureCollectionUpdate([], IsReadyForValidation);

        var newMatches = new List<DatabaseCaptureMatch>();
        foreach (var target in _targets.Values)
        {
            if (target.IsMatched) continue;
            var normalizedCandidates = target.Normalize(capturedBytes);
            try
            {
                foreach (var normalized in normalizedCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (target.HasPendingKey(normalized.Key)) continue;
                    var match = target.FindMatch(normalized.Key, counters, cancellationToken);
                    if (match is null) continue;
                    newMatches.Add(target.RecordPending(
                        normalized.Key,
                        match,
                        normalized.Representation,
                        captured.CallpointName));
                    break;
                }
            }
            finally
            {
                foreach (var normalized in normalizedCandidates) normalized.Dispose();
            }
        }
        return new CaptureCollectionUpdate(
            Array.AsReadOnly(newMatches.ToArray()),
            IsReadyForValidation);
    }

    public byte[] CopyPendingKey(DatabaseCaptureMatch match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(match);
        return RequiredTarget(match.DatabaseId).CopyPendingKey(match);
    }

    public void ConfirmExport(DatabaseCaptureMatch match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(match);
        RequiredTarget(match.DatabaseId).ConfirmExport(match);
    }

    public void Release(DatabaseCaptureMatch match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(match);
        RequiredTarget(match.DatabaseId).ReleaseSensitiveBuffers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var target in _targets.Values) target.Dispose();
        _targets.Clear();
        _disposed = true;
    }

    private DatabaseCaptureTarget RequiredTarget(string databaseId) =>
        _targets.TryGetValue(databaseId, out var target)
            ? target
            : throw new ArgumentException("Database target does not belong to this collector.", nameof(databaseId));
}
