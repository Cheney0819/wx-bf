namespace Wx411.Core.Windows;

public interface ICallpointCaptureBackend
{
    Task<CapturedKeyMaterial?> CaptureAsync(
        int pid,
        string dllPath,
        CallpointDefinition callpoint,
        CancellationToken ct = default);

    Task<CapturedKeyMaterial?> CaptureAnyAsync(
        int pid,
        string dllPath,
        IReadOnlyList<CallpointDefinition> callpoints,
        CancellationToken ct = default);

    Task<CapturedKeyMaterial?> CaptureAnyUntilAcceptedAsync(
        int pid,
        string dllPath,
        IReadOnlyList<CallpointDefinition> callpoints,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default);

    Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default);

    Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsUntilAcceptedAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default);

    Task<CapturedKeyMaterial?> CaptureToChannelWhenModuleLoadsAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        CapturedCandidateChannel channel,
        Func<bool> shouldStop,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default);
}

public sealed record CallpointCaptureStatus(string Message, string Detail);

public sealed class FakeCaptureBackend : ICallpointCaptureBackend
{
    private readonly CapturedKeyMaterial? _result;

    public FakeCaptureBackend(CapturedKeyMaterial? result) => _result = result;

    public Task<CapturedKeyMaterial?> CaptureAsync(
        int pid, string dllPath, CallpointDefinition callpoint, CancellationToken ct = default)
        => Task.FromResult(_result);

    public Task<CapturedKeyMaterial?> CaptureAnyAsync(
        int pid, string dllPath, IReadOnlyList<CallpointDefinition> callpoints, CancellationToken ct = default)
        => Task.FromResult(_result);

    public Task<CapturedKeyMaterial?> CaptureAnyUntilAcceptedAsync(
        int pid,
        string dllPath,
        IReadOnlyList<CallpointDefinition> callpoints,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(_result is not null && acceptCapture(_result) ? _result : null);

    public Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(_result);

    public Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsUntilAcceptedAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(_result is not null && acceptCapture(_result) ? _result : null);

    public Task<CapturedKeyMaterial?> CaptureToChannelWhenModuleLoadsAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        CapturedCandidateChannel channel,
        Func<bool> shouldStop,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_result?.KeyData is { Length: > 0 } data)
        {
            var copy = new CapturedKeyMaterial(
                _result.CallpointName,
                _result.HitRva,
                _result.RegisterValues,
                _result.Pid,
                _result.CapturedAt)
            {
                KeyData = data.ToArray(),
                KeyLength = _result.KeyLength,
                Error = _result.Error,
            };
            channel.TryWrite(copy);
        }
        return Task.FromResult<CapturedKeyMaterial?>(null);
    }
}
