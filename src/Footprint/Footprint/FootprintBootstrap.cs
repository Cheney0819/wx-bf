using System.Diagnostics;

namespace Footprint;

public static class FootprintBootstrap
{
    private static readonly IFootprintBootstrapRuntime DefaultRuntime = new FootprintBootstrapRuntime();
    private static readonly Action<string> DefaultDiagnosticSink = static message => Trace.WriteLine(message);
    private static readonly AsyncLocal<OverrideFrame<IFootprintBootstrapRuntime>?> RuntimeOverrides = new();
    private static readonly AsyncLocal<OverrideFrame<Action<string>>?> DiagnosticOverrides = new();

    public static Task StartSilentAsync()
    {
        var runtime = Resolve(RuntimeOverrides.Value, DefaultRuntime);
        var diagnosticSink = Resolve(DiagnosticOverrides.Value, DefaultDiagnosticSink);
        _ = Task.Run(() => ObserveBackgroundAsync(runtime, diagnosticSink));
        return Task.CompletedTask;
    }

    internal static IFootprintBootstrapRuntime CurrentRuntimeForTests =>
        Resolve(RuntimeOverrides.Value, DefaultRuntime);

    internal static int RuntimeOverrideFrameCountForTests => Count(RuntimeOverrides.Value);

    internal static IDisposable UseRuntimeForTests(IFootprintBootstrapRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var frame = new OverrideFrame<IFootprintBootstrapRuntime>(runtime, RuntimeOverrides.Value);
        RuntimeOverrides.Value = frame;
        return new OverrideScope<IFootprintBootstrapRuntime>(RuntimeOverrides, frame);
    }

    internal static IDisposable UseDiagnosticSinkForTests(Action<string> diagnosticSink)
    {
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        var frame = new OverrideFrame<Action<string>>(diagnosticSink, DiagnosticOverrides.Value);
        DiagnosticOverrides.Value = frame;
        return new OverrideScope<Action<string>>(DiagnosticOverrides, frame);
    }

    private static async Task ObserveBackgroundAsync(IFootprintBootstrapRuntime runtime, Action<string> diagnosticSink)
    {
        try
        {
            await runtime.EnsureBackgroundAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                diagnosticSink("后台启动任务发生异常。");
            }
            catch (Exception)
            {
            }
        }
    }

    private static T Resolve<T>(OverrideFrame<T>? frame, T fallback)
    {
        while (frame is not null)
        {
            if (!frame.IsDisposed) return frame.Value;
            frame = frame.Previous;
        }
        return fallback;
    }

    private static int Count<T>(OverrideFrame<T>? frame)
    {
        var count = 0;
        while (frame is not null)
        {
            count++;
            frame = frame.Previous;
        }
        return count;
    }

    private static OverrideFrame<T>? SkipDisposed<T>(OverrideFrame<T>? frame)
    {
        while (frame?.IsDisposed == true) frame = frame.Previous;
        return frame;
    }

    private sealed class OverrideFrame<T>(T value, OverrideFrame<T>? previous)
    {
        private int _disposed;

        public T Value { get; } = value;
        public OverrideFrame<T>? Previous { get; } = previous;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class OverrideScope<T>(AsyncLocal<OverrideFrame<T>?> owner, OverrideFrame<T> frame) : IDisposable
    {
        public void Dispose()
        {
            frame.Dispose();
            owner.Value = SkipDisposed(owner.Value);
        }
    }
}
