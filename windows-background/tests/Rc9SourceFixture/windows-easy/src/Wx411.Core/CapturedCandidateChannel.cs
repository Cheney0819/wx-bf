using System.Threading.Channels;

namespace Wx411.Core;

public enum CaptureSessionErrorKind
{
    CandidateQueueOverflow,
    DebuggerState,
    BreakpointRestore,
}

public sealed record CaptureSessionError(
    CaptureSessionErrorKind Kind,
    string Message,
    int? Pid = null);

public sealed class CapturedCandidateChannel : IDisposable, IAsyncDisposable
{
    public const int Capacity = 64;
    public const int MaxPayloadBytes = 4096;

    private readonly Channel<CapturedKeyMaterial> _channel = Channel.CreateBounded<CapturedKeyMaterial>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private bool _completed;
    private bool _disposed;

    public CaptureSessionError? Error { get; private set; }

    public bool TryWrite(CapturedKeyMaterial candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (candidate.KeyData is not { Length: > 0 } payload ||
            payload.Length > MaxPayloadBytes ||
            candidate.KeyLength is <= 0 or > MaxPayloadBytes)
        {
            candidate.Dispose();
            return false;
        }

        if (!_completed && _channel.Writer.TryWrite(candidate))
            return true;

        Error ??= new CaptureSessionError(
            CaptureSessionErrorKind.CandidateQueueOverflow,
            $"Captured candidate queue reached its fixed capacity of {Capacity}.",
            candidate.Pid);
        candidate.Dispose();
        return false;
    }

    public IAsyncEnumerable<CapturedKeyMaterial> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Complete();
        while (_channel.Reader.TryRead(out var candidate))
            candidate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
