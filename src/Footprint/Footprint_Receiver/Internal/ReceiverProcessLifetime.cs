using System.Runtime.InteropServices;

namespace Footprint.Receiver.Internal;

internal sealed class ReceiverProcessLifetime : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private PosixSignalRegistration? _termination;

    private ReceiverProcessLifetime() { }

    public CancellationToken Token => _stopping.Token;

    public static ReceiverProcessLifetime Create()
    {
        var lifetime = new ReceiverProcessLifetime();
        Console.CancelKeyPress += lifetime.OnCancelKeyPress;
        if (!OperatingSystem.IsWindows())
        {
            lifetime._termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                lifetime.RequestStop();
            });
        }
        return lifetime;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        RequestStop();
    }

    private void RequestStop()
    {
        try { _stopping.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _termination?.Dispose();
        _stopping.Dispose();
    }
}
