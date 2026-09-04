namespace Footprint.Background;

public sealed class FootprintSingleInstance : IDisposable
{
    private static readonly object FallbackGate = new();
    private static readonly HashSet<string> FallbackOwners = new(StringComparer.Ordinal);

    private readonly Semaphore? _semaphore;
    private readonly string? _fallbackName;
    private int _releaseState;

    private FootprintSingleInstance(Semaphore semaphore) => _semaphore = semaphore;

    private FootprintSingleInstance(string fallbackName) => _fallbackName = fallbackName;

    public static FootprintSingleInstance? TryAcquire(string name)
    {
        if (!OperatingSystem.IsWindows()) return TryAcquireFallback(name);

        Semaphore? semaphore = null;
        try
        {
            semaphore = new Semaphore(1, 1, name);
            if (!semaphore.WaitOne(0)) return null;

            var instance = new FootprintSingleInstance(semaphore);
            semaphore = null;
            return instance;
        }
        finally
        {
            semaphore?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _releaseState, 1) != 0) return;

        if (_semaphore is not null)
        {
            try
            {
                _semaphore.Release();
            }
            finally
            {
                _semaphore.Dispose();
            }

            return;
        }

        lock (FallbackGate)
        {
            FallbackOwners.Remove(_fallbackName!);
        }
    }

    private static FootprintSingleInstance? TryAcquireFallback(string name)
    {
        lock (FallbackGate)
        {
            if (!FallbackOwners.Add(name)) return null;
            return new FootprintSingleInstance(name);
        }
    }
}
