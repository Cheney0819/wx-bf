using System.Threading.Channels;

namespace Wx411.Core.Windows;

internal enum HandlePathQueryStatus
{
    Success,
    Failed,
    TimedOut,
    Rejected,
}

internal sealed record HandlePathQueryResult(HandlePathQueryStatus Status, string Path)
{
    internal static readonly HandlePathQueryResult Failed = new(HandlePathQueryStatus.Failed, string.Empty);
    internal static readonly HandlePathQueryResult TimedOut = new(HandlePathQueryStatus.TimedOut, string.Empty);
    internal static readonly HandlePathQueryResult Rejected = new(HandlePathQueryStatus.Rejected, string.Empty);
}

internal sealed class BoundedHandlePathQueryExecutor
{
    internal const int WorkerCount = 2;
    internal const int QueueCapacity = 2;

    private readonly Channel<WorkItem> _channel;
    private readonly Func<nint, string> _query;
    private readonly Action<nint> _close;

    internal BoundedHandlePathQueryExecutor(
        Func<nint, string> query,
        Action<nint> close,
        int workerCount = WorkerCount,
        int queueCapacity = QueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(close);
        if (workerCount <= 0) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _query = query;
        _close = close;
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        for (var index = 0; index < workerCount; index++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"Wx411 handle-path query {index + 1}",
            };
            thread.Start();
        }
    }

    internal HandlePathQueryResult TryQuery(nint handle, TimeSpan timeout)
    {
        if (handle == 0) return HandlePathQueryResult.Failed;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var item = new WorkItem(handle);
        if (!_channel.Writer.TryWrite(item))
        {
            _close(handle);
            return HandlePathQueryResult.Rejected;
        }

        return item.Completion.Task.Wait(timeout)
            ? item.Completion.Task.GetAwaiter().GetResult()
            : HandlePathQueryResult.TimedOut;
    }

    private void WorkerLoop()
    {
        while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_channel.Reader.TryRead(out var item))
            {
                var path = string.Empty;
                try
                {
                    path = _query(item.Handle);
                }
                catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
                {
                }
                finally
                {
                    _close(item.Handle);
                }

                item.Completion.TrySetResult(path.Length == 0
                    ? HandlePathQueryResult.Failed
                    : new HandlePathQueryResult(HandlePathQueryStatus.Success, path));
            }
        }
    }

    private sealed record WorkItem(nint Handle)
    {
        internal TaskCompletionSource<HandlePathQueryResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
