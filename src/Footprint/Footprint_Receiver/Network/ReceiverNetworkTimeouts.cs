using System.Globalization;

namespace Footprint.Receiver.Network;

public sealed record ReceiverNetworkTimeouts(TimeSpan RequestTimeout, TimeSpan StreamIdleTimeout)
{
    public static ReceiverNetworkTimeouts Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

    public static ReceiverNetworkTimeouts FromEnvironment() => new(
        Read("FOOTPRINT_RECEIVER_REQUEST_TIMEOUT_SECONDS", Default.RequestTimeout),
        Read("FOOTPRINT_RECEIVER_STREAM_IDLE_TIMEOUT_SECONDS", Default.StreamIdleTimeout));

    private static TimeSpan Read(string name, TimeSpan fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.FromHours(24).TotalSeconds)
            throw new InvalidOperationException($"环境变量 {name} 必须大于 0 且不超过 86400 秒。");
        return TimeSpan.FromSeconds(seconds);
    }
}

internal sealed class IdleTimeoutReadStream(Stream inner, TimeSpan idleTimeout) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await inner.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new OperationCanceledException("接收端 HTTP 响应流超过停滞期限。", exception, timeout.Token);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
