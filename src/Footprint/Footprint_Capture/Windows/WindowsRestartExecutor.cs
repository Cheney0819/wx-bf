using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Footprint.Core.Capture;
using Footprint.Core.Contracts;
using Footprint.Core.State;
using Microsoft.Win32.SafeHandles;

namespace Footprint.Capture.Windows;

internal interface IRestartClock
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    Task<T> RunWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IWindowsRestartProcessApi
{
    WindowsRestartTargetOpenResult TryOpenVerifiedTarget(WeixinProcessSnapshot snapshot);
}

internal enum WindowsRestartTargetOpenStatus { Opened, Missing, IdentityMismatch, ObservationFailed }
internal enum WindowsRestartControlStatus { Applied, AlreadyStopped, NormalExitUnavailable }
internal sealed record WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus Status,
    IWindowsRestartTargetLease? Lease, string Code);
internal interface IWindowsRestartTargetLease : IDisposable
{
    WindowsRestartControlStatus RequestNormalExit();
    WindowsRestartControlStatus KillProcessTree();
}

public sealed class WindowsRestartExecutor : IRestartExecutor
{
    internal static readonly TimeSpan NormalExitTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ForceExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int MaximumEvents = 16;

    private readonly IFootprintStateStore _stateStore;
    private readonly IWeixinRestartObserver _observer;
    private readonly IWindowsRestartProcessApi _processes;
    private readonly IRestartClock _clock;
    private readonly IFridaSpawnPort _frida;

    public WindowsRestartExecutor(IFootprintStateStore stateStore, IWeixinRestartObserver observer,
        IFridaSpawnPort frida)
        : this(stateStore, observer, new SystemWindowsRestartProcessApi(), new SystemRestartClock(), frida)
    {
    }

    internal WindowsRestartExecutor(IFootprintStateStore stateStore, IWeixinRestartObserver observer,
        IWindowsRestartProcessApi processes, IRestartClock clock, IFridaSpawnPort frida)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _frida = frida ?? throw new ArgumentNullException(nameof(frida));
    }

    public async Task<RestartExecutionResult> ExecuteAsync(RestartExecutionRequest request,
        IProgress<FootprintEvent> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var rejection = Validate(request);
        if (rejection is not null)
        {
            if (HasReportableSequence(request) && !string.IsNullOrWhiteSpace(request.RunId))
            {
                var rejectionEvents = new RestartEventWriter(progress, request.RunId,
                    request.FirstDeviceSequence, request.FirstRunSequence, _clock);
                rejectionEvents.Failure(rejection.Code);
            }
            return rejection;
        }

        var events = new RestartEventWriter(progress, request.RunId, request.FirstDeviceSequence,
            request.FirstRunSequence, _clock);

        try
        {
            _ = GetVerifiedSnapshots(await ObserveCheckedAsync(request, cancellationToken));
        }
        catch (ProcessIdentityMismatchException)
        {
            events.Failure("restart_process_identity_mismatch");
            return Failure("restart_process_identity_mismatch", "微信进程身份与已验证采集代际不匹配。");
        }
        catch (ProcessObservationFailedException)
        {
            events.Failure("restart_process_observation_failed");
            return Failure("restart_process_observation_failed", "无法完成微信进程身份观察。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(events);
        }

        var consumedAtUtc = _clock.UtcNow;
        var budget = new RestartBudgetRecord(
            request.Generation.BudgetKey,
            request.RequestKind == RestartRequestKind.Automatic ? "automatic" : "manual",
            request.RequestKind == RestartRequestKind.Automatic ? null : request.CommandId,
            consumedAtUtc,
            consumedAtUtc.AddMinutes(5));

        bool consumed;
        try
        {
            consumed = await _stateStore.TryConsumeRestartBudgetAsync(budget, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(events);
        }
        catch (Exception)
        {
            events.Failure("restart_budget_transaction_failed");
            return Failure("restart_budget_transaction_failed", "重启预算交易失败。");
        }

        if (!consumed)
        {
            events.Failure("restart_budget_unavailable");
            return new RestartExecutionResult(RestartExecutionStatus.BudgetUnavailable,
                "restart_budget_unavailable", "当前采集代际的重启预算已消耗。");
        }

        IReadOnlyList<WeixinProcessSnapshot> restartTargets = [];
        IReadOnlyList<WeixinProcessSnapshot> remaining;
        var normalObservationTimedOut = false;
        WaitObservationResult finalObservationWindow;
        try
        {
            restartTargets = GetVerifiedSnapshots(await ObserveCheckedAsync(request, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            events.Running("request_normal_exit", "请求正常退出",
                "restart_request_normal_exit", "正在请求微信正常退出");
            foreach (var target in restartTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refreshed = GetVerifiedSnapshots(await ObserveCheckedAsync(request, cancellationToken))
                    .FirstOrDefault(snapshot => snapshot.ProcessId == target.ProcessId);
                if (refreshed is null) continue;
                var opened = _processes.TryOpenVerifiedTarget(refreshed);
                if (opened.Status == WindowsRestartTargetOpenStatus.Missing) continue;
                if (opened.Status == WindowsRestartTargetOpenStatus.IdentityMismatch)
                    throw new ProcessIdentityMismatchException();
                if (opened.Status != WindowsRestartTargetOpenStatus.Opened || opened.Lease is null)
                    throw new ProcessObservationFailedException();
                using (opened.Lease)
                {
                    var normalExit = opened.Lease.RequestNormalExit();
                    if (normalExit == WindowsRestartControlStatus.NormalExitUnavailable)
                    {
                        events.Running("kill_no_main_window", "结束托盘驻留进程",
                            "restart_kill_no_main_window", "未找到可关闭的微信主窗口，正在结束微信进程树");
                        _ = opened.Lease.KillProcessTree();
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            events.Running("wait_normal_exit", "等待正常退出",
                "restart_wait_normal_exit", "正在等待微信退出");
            var wait = await WaitUntilStoppedAsync(request, NormalExitTimeout, cancellationToken);
            if (wait.ObservationTimedOut)
            {
                events.Running("normal_observation_timeout_force", "正常退出观察超时",
                    "restart_normal_observation_timeout_force", "正常退出观察超时，正在结束已验证微信进程");
                remaining = restartTargets;
                normalObservationTimedOut = true;
                finalObservationWindow = wait;
            }
            else
            {
                remaining = wait.Snapshots;
                finalObservationWindow = wait;
            }
        }
        catch (ProcessIdentityMismatchException)
        {
            events.Failure("restart_process_identity_mismatch");
            return Failure("restart_process_identity_mismatch", "微信进程身份与已验证采集代际不匹配。");
        }
        catch (ProcessObservationFailedException)
        {
            events.Failure("restart_process_observation_failed");
            return Failure("restart_process_observation_failed", "无法完成微信进程身份观察。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(events);
        }
        catch (Exception)
        {
            events.Failure("restart_process_control_failed");
            return Failure("restart_process_control_failed", "微信进程控制失败。");
        }

        if (remaining.Count > 0)
        {
            var killFailed = false;
            events.Running("kill_remaining_tree", "结束残留进程树",
                "restart_kill_remaining_tree", "正在结束残留微信进程");
            try
            {
                foreach (var target in remaining.OrderBy(snapshot => snapshot.ProcessId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var refreshed = normalObservationTimedOut
                        ? target
                        : GetVerifiedSnapshots(await ObserveCheckedAsync(request, cancellationToken))
                            .FirstOrDefault(snapshot => snapshot.ProcessId == target.ProcessId);
                    if (refreshed is null) continue;
                    try
                    {
                        var opened = _processes.TryOpenVerifiedTarget(refreshed);
                        if (opened.Status == WindowsRestartTargetOpenStatus.Missing) continue;
                        if (opened.Status == WindowsRestartTargetOpenStatus.IdentityMismatch)
                            throw new ProcessIdentityMismatchException();
                        if (opened.Status != WindowsRestartTargetOpenStatus.Opened || opened.Lease is null)
                        {
                            killFailed = true;
                            continue;
                        }
                        using (opened.Lease) _ = opened.Lease.KillProcessTree();
                    }
                    catch (ProcessIdentityMismatchException) { throw; }
                    catch (Exception)
                    {
                        killFailed = true;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(events);
            }
            catch (ProcessIdentityMismatchException)
            {
                events.Failure("restart_process_identity_mismatch");
                return Failure("restart_process_identity_mismatch", "微信进程身份与已验证采集代际不匹配。");
            }
            catch (Exception)
            {
                events.Failure("restart_process_observation_failed");
                return Failure("restart_process_observation_failed", "无法在结束前重新确认微信进程身份。");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Running("wait_force_exit", "等待强制退出",
                    "restart_wait_force_exit", "正在结束残留微信进程");
                var wait = await WaitUntilStoppedAsync(request, ForceExitTimeout, cancellationToken);
                if (wait.ObservationTimedOut)
                {
                    events.Timeout("restart_force_observation_timeout");
                    return new RestartExecutionResult(RestartExecutionStatus.TimedOut,
                        "restart_force_observation_timeout", "微信强制退出观察超时。");
                }
                remaining = wait.Snapshots;
                finalObservationWindow = wait;
            }
            catch (ProcessIdentityMismatchException)
            {
                events.Failure("restart_process_identity_mismatch");
                return Failure("restart_process_identity_mismatch", "微信进程身份与已验证采集代际不匹配。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(events);
            }
            catch (Exception)
            {
                events.Failure("restart_process_observation_failed");
                return Failure("restart_process_observation_failed", "无法确认微信进程已完全退出。");
            }

            if (remaining.Count > 0)
            {
                events.Timeout("restart_force_exit_timeout");
                return new RestartExecutionResult(RestartExecutionStatus.TimedOut,
                    "restart_force_exit_timeout", "微信进程在强制退出后五秒内仍未停止。");
            }
            if (killFailed)
            {
                events.Failure("restart_kill_partial_failure");
                return Failure("restart_kill_partial_failure", "部分微信进程树结束操作失败。");
            }
        }

        try
        {
            var finalObservation = await ObserveWithinWindowAsync(request, finalObservationWindow,
                cancellationToken);
            if (finalObservation is null)
            {
                events.Timeout("restart_pre_spawn_observation_timeout");
                return new RestartExecutionResult(RestartExecutionStatus.TimedOut,
                    "restart_pre_spawn_observation_timeout", "微信启动前的最终进程观察超时。");
            }
            if (GetVerifiedSnapshots(finalObservation).Count > 0)
            {
                events.Failure("restart_process_reappeared");
                return Failure("restart_process_reappeared", "微信进程在启动前重新出现，已停止重启。");
            }
        }
        catch (ProcessIdentityMismatchException)
        {
            events.Failure("restart_process_identity_mismatch");
            return Failure("restart_process_identity_mismatch", "微信进程身份与已验证采集代际不匹配。");
        }
        catch (ProcessObservationFailedException)
        {
            events.Failure("restart_process_observation_failed");
            return Failure("restart_process_observation_failed", "无法完成微信启动前的最终进程观察。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(events);
        }

        IFridaCaptureSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Running("frida_spawn", "重新启动微信",
                "restart_frida_spawn", "正在重新启动微信");
            session = await _frida.SpawnAsync(request.Installation.ExecutablePath, request.ProfilePath,
                request.OutputDirectory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeSessionQuietlyAsync(session);
            return Cancelled(events);
        }
        catch (Exception)
        {
            await DisposeSessionQuietlyAsync(session);
            events.Failure("restart_frida_spawn_failed");
            return Failure("restart_frida_spawn_failed", "微信重新启动失败。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Running("wait_key_capture", "等待密钥捕获",
                "restart_wait_key_capture", "正在等待密钥捕获");
            if (!await session.WaitForKeyCaptureAsync(cancellationToken))
            {
                await DisposeSessionQuietlyAsync(session);
                events.Failure("restart_key_capture_failed");
                return Failure("restart_key_capture_failed", "密钥捕获失败。");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeSessionQuietlyAsync(session);
            return Cancelled(events);
        }
        catch (Exception)
        {
            await DisposeSessionQuietlyAsync(session);
            events.Failure("restart_key_capture_failed");
            return Failure("restart_key_capture_failed", "密钥捕获失败。");
        }

        events.Success();
        return new RestartExecutionResult(RestartExecutionStatus.Succeeded,
            "restart_succeeded", "微信重启完成。", session);
    }

    private async Task<WaitObservationResult> WaitUntilStoppedAsync(
        RestartExecutionRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        IReadOnlyList<WeixinProcessSnapshot> lastVerified = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = _clock.GetElapsedTime(started);
            if (elapsed >= timeout) return new WaitObservationResult(lastVerified, false, started, timeout);
            var remainingTime = timeout - elapsed;
            WeixinObservationResult observation;
            try
            {
                observation = await _clock.RunWithTimeoutAsync(
                    token => ObserveCheckedAsync(request, token), remainingTime, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new WaitObservationResult(lastVerified, true, started, timeout);
            }

            var remaining = GetVerifiedSnapshots(observation);
            if (remaining.Count == 0) return new WaitObservationResult([], false, started, timeout);
            lastVerified = remaining;
            elapsed = _clock.GetElapsedTime(started);
            if (elapsed >= timeout) return new WaitObservationResult(lastVerified, false, started, timeout);
            remainingTime = timeout - elapsed;
            await _clock.DelayAsync(remainingTime < PollInterval ? remainingTime : PollInterval, cancellationToken);
        }
    }

    private async Task<WeixinObservationResult?> ObserveWithinWindowAsync(RestartExecutionRequest request,
        WaitObservationResult window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = _clock.GetElapsedTime(window.StartedTimestamp);
        if (elapsed >= window.Timeout) return null;
        try
        {
            return await _clock.RunWithTimeoutAsync(token => ObserveCheckedAsync(request, token),
                window.Timeout - elapsed, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task<WeixinObservationResult> ObserveCheckedAsync(
        RestartExecutionRequest request, CancellationToken cancellationToken)
    {
        WeixinObservationResult observation;
        try { observation = await _observer.ObserveAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ProcessObservationFailedException(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Status == WeixinObservationStatus.IdentityMismatch)
            throw new ProcessIdentityMismatchException();
        if (observation.Status == WeixinObservationStatus.ObservationFailed)
            throw new ProcessObservationFailedException();
        if (observation.Status == WeixinObservationStatus.NoProcess) return observation;
        if (observation.Status != WeixinObservationStatus.Verified)
            throw new ProcessObservationFailedException();

        foreach (var snapshot in observation.Snapshots)
        {
            if (!WindowsWeixinProbe.CanonicalPathsEqual(snapshot.ExecutablePath, request.Installation.ExecutablePath))
                throw new ProcessIdentityMismatchException();
            if (!HashEquals(snapshot.InstallationDllSha256, request.Generation.WeixinDllSha256) ||
                !HashEquals(snapshot.LoadedModuleFileSha256, request.Generation.WeixinDllSha256))
                throw new ProcessIdentityMismatchException();
        }
        return observation;
    }

    private static IReadOnlyList<WeixinProcessSnapshot> GetVerifiedSnapshots(WeixinObservationResult observation) =>
        observation.Status == WeixinObservationStatus.NoProcess
            ? []
            : observation.Snapshots
            .GroupBy(snapshot => snapshot.ProcessId)
            .Select(group => group.First())
            .OrderBy(snapshot => snapshot.ProcessId)
            .ToArray();

    private static RestartExecutionResult? Validate(RestartExecutionRequest request)
    {
        if (request.FirstDeviceSequence <= 0 || request.FirstRunSequence <= 0 ||
            request.FirstDeviceSequence > long.MaxValue - MaximumEvents ||
            request.FirstRunSequence > long.MaxValue - MaximumEvents)
            return Rejected("restart_event_sequence_invalid", "重启事件序号无效。");
        if (request.Decision is null || request.Generation is null || request.Installation is null ||
            request.ProfileSelection is null)
            return Rejected("restart_request_invalid", "重启请求参数无效。");
        if (!request.Decision.IsAllowed)
            return Rejected("restart_decision_not_allowed", "重启决策未允许执行。");
        if (request.RequestKind == RestartRequestKind.Automatic &&
            (request.Decision.Kind != RestartDecisionKind.AllowAutomatic || request.CommandId is not null))
            return Rejected("restart_automatic_request_invalid", "自动重启请求与决策不匹配。");
        if (request.RequestKind == RestartRequestKind.Manual &&
            (request.Decision.Kind != RestartDecisionKind.AllowManual || string.IsNullOrWhiteSpace(request.CommandId)))
            return Rejected("restart_manual_request_invalid", "手动重启请求与决策不匹配。");
        if (!Enum.IsDefined(request.RequestKind))
            return Rejected("restart_request_kind_invalid", "重启请求类型无效。");
        if (!request.ProfileSelection.Accepted || !request.ProfileSelection.MayControlProcess ||
            request.ProfileSelection.Profile is null)
            return Rejected("restart_profile_not_verified", "微信版本配置未通过校验。");
        if (!IsSha256(request.Generation.WeixinDllSha256) ||
            !IsSha256(request.ProfileSelection.DllSha256) ||
            !HashEquals(request.ProfileSelection.DllSha256, request.Generation.WeixinDllSha256))
            return Rejected("restart_profile_generation_mismatch", "微信版本配置与当前采集代际不匹配。");
        if (string.IsNullOrWhiteSpace(request.RunId) ||
            string.IsNullOrWhiteSpace(request.Installation.ExecutablePath) ||
            string.IsNullOrWhiteSpace(request.Installation.DllPath) ||
            string.IsNullOrWhiteSpace(request.ProfilePath) ||
            string.IsNullOrWhiteSpace(request.OutputDirectory))
            return Rejected("restart_request_invalid", "重启请求参数无效。");
        return null;
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool HasReportableSequence(RestartExecutionRequest request) =>
        request.FirstDeviceSequence > 0 && request.FirstRunSequence > 0 &&
        request.FirstDeviceSequence <= long.MaxValue - MaximumEvents &&
        request.FirstRunSequence <= long.MaxValue - MaximumEvents;

    private static RestartExecutionResult Rejected(string code, string messageZh) =>
        new(RestartExecutionStatus.Rejected, code, messageZh);

    private static RestartExecutionResult Failure(string code, string messageZh) =>
        new(RestartExecutionStatus.Failed, code, messageZh);

    private static RestartExecutionResult Cancelled(RestartEventWriter events)
    {
        events.Cancelled();
        return new RestartExecutionResult(RestartExecutionStatus.Cancelled,
            "restart_cancelled", "微信重启已取消。");
    }

    private static async ValueTask DisposeSessionQuietlyAsync(IFridaCaptureSession? session)
    {
        if (session is null) return;
        try { await session.DisposeAsync(); }
        catch (Exception) { }
    }

    private sealed class ProcessIdentityMismatchException : Exception;
    private sealed class ProcessObservationFailedException : Exception;
    private sealed record WaitObservationResult(IReadOnlyList<WeixinProcessSnapshot> Snapshots,
        bool ObservationTimedOut, long StartedTimestamp, TimeSpan Timeout);

    private sealed class RestartEventWriter(
        IProgress<FootprintEvent> progress,
        string runId,
        long firstDeviceSequence,
        long firstRunSequence,
        IRestartClock clock)
    {
        private long _deviceSequence = firstDeviceSequence;
        private long _runSequence = firstRunSequence;

        public void Running(string step, string stepNameZh, string code, string messageZh) =>
            Report(step, "running", stepNameZh, code, messageZh);

        public void Failure(string code) =>
            Report("failed", "failed", "微信重启失败", code, "微信重启失败");

        public void Timeout(string code) =>
            Report("force_exit_timeout", "timed_out", "强制退出超时", code, "微信重启失败");

        public void Cancelled() =>
            Report("cancelled", "cancelled", "微信重启取消", "restart_cancelled", "微信重启已取消");

        public void Success() =>
            Report("completed", "succeeded", "微信重启完成", "restart_succeeded", "微信重启完成");

        private void Report(string step, string status, string stepNameZh, string code, string messageZh)
        {
            progress.Report(new FootprintEvent(
                $"Footprint_Event_{Guid.NewGuid():N}", runId, _deviceSequence++, _runSequence++,
                "Footprint_Capture", FootprintStage.Footprint_WeixinRestart, step, status, "微信重启",
                stepNameZh,
                status switch
                {
                    "running" => "运行中",
                    "succeeded" => "已完成",
                    "cancelled" => "已取消",
                    "timed_out" => "已超时",
                    _ => "已失败"
                },
                status == "succeeded" ? 1 : 0, code, messageZh, clock.UtcNow));
        }
    }
}

internal sealed class SystemRestartClock : IRestartClock
{
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
    public long GetTimestamp() => _timeProvider.GetTimestamp();
    public TimeSpan GetElapsedTime(long startingTimestamp) => _timeProvider.GetElapsedTime(startingTimestamp);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
    public async Task<T> RunWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = Task.Run(() => operation(timeoutCancellation.Token), CancellationToken.None);
        try { return await task.WaitAsync(timeout, _timeProvider, cancellationToken); }
        catch (TimeoutException)
        {
            timeoutCancellation.Cancel();
            ObserveLegacyFault(task);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timeoutCancellation.Cancel();
            ObserveLegacyFault(task);
            throw;
        }
    }

    private static void ObserveLegacyFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

internal sealed class SystemWindowsRestartProcessApi : IWindowsRestartProcessApi
{
    private readonly IWindowsRestartProcessFactory _factory;
    private readonly IWindowsRestartNativeProcessApi _native;

    public SystemWindowsRestartProcessApi()
        : this(new SystemWindowsRestartProcessFactory(), new SystemWindowsRestartNativeProcessApi()) { }

    internal SystemWindowsRestartProcessApi(IWindowsRestartProcessFactory factory,
        IWindowsRestartNativeProcessApi native)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public WindowsRestartTargetOpenResult TryOpenVerifiedTarget(WeixinProcessSnapshot snapshot)
    {
        IWindowsRestartProcessHandle handle;
        try { handle = _factory.Open(snapshot.ProcessId); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.Missing, null,
                "restart_target_missing");
        }
        IWindowsRestartNativeHandleLease? nativeLease = null;
        try
        {
            if (handle.HasExited)
            {
                handle.Dispose();
                return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.Missing, null,
                    "restart_target_stopped");
            }
            nativeLease = _native.Acquire(handle.SafeHandle);
            var executablePath = _native.QueryFullProcessImageName(nativeLease);
            if (!WindowsWeixinProbe.CanonicalPathsEqual(executablePath, snapshot.ExecutablePath))
            {
                nativeLease.Dispose();
                handle.Dispose();
                return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.IdentityMismatch, null,
                    "restart_target_identity_changed");
            }
            return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.Opened,
                new SystemWindowsRestartTargetLease(handle, nativeLease), "restart_target_opened");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            nativeLease?.Dispose();
            handle.Dispose();
            return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.Missing, null,
                "restart_target_stopped");
        }
        catch (Exception)
        {
            nativeLease?.Dispose();
            handle.Dispose();
            return new WindowsRestartTargetOpenResult(WindowsRestartTargetOpenStatus.ObservationFailed, null,
                "restart_target_open_failed");
        }
    }
}

internal interface IWindowsRestartProcessFactory { IWindowsRestartProcessHandle Open(int processId); }
internal interface IWindowsRestartProcessHandle : IDisposable
{
    SafeProcessHandle SafeHandle { get; }
    bool HasExited { get; }
    bool CloseMainWindow();
    void KillProcessTree();
}

internal interface IWindowsRestartNativeHandleLease : IDisposable
{
    SafeProcessHandle Handle { get; }
    bool IsDisposed { get; }
}

internal interface IWindowsRestartNativeProcessApi
{
    IWindowsRestartNativeHandleLease Acquire(SafeProcessHandle processHandle);
    string QueryFullProcessImageName(IWindowsRestartNativeHandleLease handle);
}

internal sealed class SystemWindowsRestartProcessFactory : IWindowsRestartProcessFactory
{
    public IWindowsRestartProcessHandle Open(int processId) =>
        new SystemWindowsRestartProcessHandle(Process.GetProcessById(processId));
}

internal sealed class SystemWindowsRestartProcessHandle(Process process) : IWindowsRestartProcessHandle
{
    public SafeProcessHandle SafeHandle => process.SafeHandle;
    public bool HasExited => process.HasExited;
    public bool CloseMainWindow() => process.CloseMainWindow();
    public void KillProcessTree() => process.Kill(entireProcessTree: true);
    public void Dispose() => process.Dispose();
}

internal sealed class SystemWindowsRestartNativeProcessApi : IWindowsRestartNativeProcessApi
{
    private const int MaximumPathCharacters = 32768;

    public IWindowsRestartNativeHandleLease Acquire(SafeProcessHandle processHandle) =>
        new PinnedWindowsRestartNativeHandleLease(processHandle);

    public string QueryFullProcessImageName(IWindowsRestartNativeHandleLease handle)
    {
        if (handle.IsDisposed) throw new ObjectDisposedException(nameof(handle));
        var buffer = new StringBuilder(MaximumPathCharacters);
        var length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageNameW(handle.Handle, 0, buffer, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString(0, checked((int)length));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle processHandle, uint flags,
        StringBuilder executablePath, ref uint size);
}

internal sealed class PinnedWindowsRestartNativeHandleLease : IWindowsRestartNativeHandleLease
{
    private readonly SafeProcessHandle _handle;
    private int _disposed;

    public PinnedWindowsRestartNativeHandleLease(SafeProcessHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        var added = false;
        _handle.DangerousAddRef(ref added);
        if (!added) throw new InvalidOperationException("Unable to pin process handle.");
    }

    public SafeProcessHandle Handle => IsDisposed
        ? throw new ObjectDisposedException(nameof(PinnedWindowsRestartNativeHandleLease))
        : _handle;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.DangerousRelease();
    }
}

internal sealed class SystemWindowsRestartTargetLease(IWindowsRestartProcessHandle handle,
    IWindowsRestartNativeHandleLease nativeHandle)
    : IWindowsRestartTargetLease
{
    public WindowsRestartControlStatus RequestNormalExit()
    {
        try
        {
            if (handle.HasExited) return WindowsRestartControlStatus.AlreadyStopped;
            return handle.CloseMainWindow()
                ? WindowsRestartControlStatus.Applied
                : WindowsRestartControlStatus.NormalExitUnavailable;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return WindowsRestartControlStatus.AlreadyStopped;
        }
    }

    public WindowsRestartControlStatus KillProcessTree()
    {
        try
        {
            if (handle.HasExited) return WindowsRestartControlStatus.AlreadyStopped;
            handle.KillProcessTree();
            return WindowsRestartControlStatus.Applied;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return WindowsRestartControlStatus.AlreadyStopped;
        }
    }

    public void Dispose()
    {
        try { nativeHandle.Dispose(); }
        finally { handle.Dispose(); }
    }
}
