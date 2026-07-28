using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wx411.Core.Windows;

public sealed class DebugCaptureBackend : ICallpointCaptureBackend, IDisposable
{
    private const string WeixinDll = "Weixin.dll";
    private const int MaxKeyBytes = 4096;
    private const int AttachRetryCount = 20;
    private const int AttachRetryDelayMs = 250;
    private static readonly TimeSpan BreakpointRestoreTimeout = TimeSpan.FromSeconds(5);
    private const uint TrapFlag = 0x100;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _moduleBase = IntPtr.Zero;
    private readonly List<ActiveBreakpoint> _breakpoints = [];
    private readonly Dictionary<uint, ActiveBreakpoint> _pendingRearms = [];
    private IntPtr _currentBpAddress;
    private bool _bpSet => _breakpoints.Count > 0;
    private bool _disposed;
    private readonly ModuleInspectionCache _moduleInspectionCache = new();
    private readonly BreakpointRestorer _breakpointRestorer = new(new NativeBreakpointRestoreOperations());
    private int _attachedPid;
    private IProgress<CallpointCaptureStatus>? _cleanupProgress;
    private CancellationToken _workerCancellationToken;

    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public void Dispose()
    {
        if (_disposed) return;
        CloseProcessHandle();
        _disposed = true;
    }

    public Task<CapturedKeyMaterial?> CaptureAsync(
        int pid,
        string dllPath,
        CallpointDefinition callpoint,
        CancellationToken ct = default) =>
        CaptureAnyAsync(pid, dllPath, [callpoint], ct);

    public Task<CapturedKeyMaterial?> CaptureAnyAsync(
        int pid,
        string dllPath,
        IReadOnlyList<CallpointDefinition> callpoints,
        CancellationToken ct = default)
    {
        if (callpoints.Count == 0)
            return Task.FromResult<CapturedKeyMaterial?>(null);

        if (!IsSupported)
            return Task.FromResult<CapturedKeyMaterial?>(Unsupported(callpoints[0], pid));

        return Task.Run(
            () => RunCaptureSync(pid, dllPath, WeixinDll, callpoints, null, null, null, null, null, null, ct),
            ct);
    }

    public Task<CapturedKeyMaterial?> CaptureAnyUntilAcceptedAsync(
        int pid,
        string dllPath,
        IReadOnlyList<CallpointDefinition> callpoints,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (callpoints.Count == 0)
            return Task.FromResult<CapturedKeyMaterial?>(null);

        if (!IsSupported)
            return Task.FromResult<CapturedKeyMaterial?>(Unsupported(callpoints[0], pid));

        return Task.Run(
            () => RunCaptureSync(pid, dllPath, WeixinDll, callpoints, null, null, progress, acceptCapture, null, null, ct),
            ct);
    }

    public Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (callpoints.Count == 0)
            return Task.FromResult<CapturedKeyMaterial?>(null);

        if (!IsSupported)
            return Task.FromResult<CapturedKeyMaterial?>(Unsupported(callpoints[0], pid));

        return Task.Run(
            () => RunCaptureSync(
                pid,
                null,
                moduleName,
                callpoints,
                moduleWaitTimeout,
                armedCaptureTimeout,
                progress,
                null,
                null,
                null,
                ct),
            ct);
    }

    public Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsUntilAcceptedAsync(
        int pid,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan moduleWaitTimeout,
        TimeSpan armedCaptureTimeout,
        Func<CapturedKeyMaterial, bool> acceptCapture,
        IProgress<CallpointCaptureStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (callpoints.Count == 0)
            return Task.FromResult<CapturedKeyMaterial?>(null);

        if (!IsSupported)
            return Task.FromResult<CapturedKeyMaterial?>(Unsupported(callpoints[0], pid));

        return Task.Run(
            () => RunCaptureSync(
                pid,
                null,
                moduleName,
                callpoints,
                moduleWaitTimeout,
                armedCaptureTimeout,
                progress,
                acceptCapture,
                null,
                null,
                ct),
            ct);
    }

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
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(shouldStop);
        if (callpoints.Count == 0) return Task.FromResult<CapturedKeyMaterial?>(null);
        if (!IsSupported) return Task.FromResult<CapturedKeyMaterial?>(Unsupported(callpoints[0], pid));
        return Task.Run(() =>
        {
            CapturedKeyMaterial? pendingTransfer = null;
            try
            {
                return RunCaptureSync(
                    pid,
                    null,
                    moduleName,
                    callpoints,
                    moduleWaitTimeout,
                    armedCaptureTimeout,
                    progress,
                    candidate =>
                    {
                        if (candidate.KeyData is not { Length: > 0 } data) return false;
                        pendingTransfer?.Dispose();
                        pendingTransfer = new CapturedKeyMaterial(
                            candidate.CallpointName,
                            candidate.HitRva,
                            candidate.RegisterValues,
                            candidate.Pid,
                            candidate.CapturedAt)
                        {
                            KeyData = data.ToArray(),
                            KeyLength = candidate.KeyLength,
                            Error = candidate.Error,
                        };
                        return false;
                    },
                    () => shouldStop() || channel.Error is not null,
                    () =>
                    {
                        var transfer = Interlocked.Exchange(ref pendingTransfer, null);
                        if (transfer is not null) channel.TryWrite(transfer);
                    },
                    ct);
            }
            finally
            {
                pendingTransfer?.Dispose();
            }
        }, ct);
    }

    private CapturedKeyMaterial? RunCaptureSync(
        int pid,
        string? dllPath,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        TimeSpan? moduleWaitTimeout,
        TimeSpan? armedCaptureTimeout,
        IProgress<CallpointCaptureStatus>? progress,
        Func<CapturedKeyMaterial, bool>? acceptCapture,
        Func<bool>? shouldStop,
        Action? afterContinue,
        CancellationToken ct)
    {
        var primaryCallpoint = callpoints[0];
        _workerCancellationToken = ct;
        var attached = false;
        var earlyAttach = dllPath is null;
        var moduleClock = Stopwatch.StartNew();
        Stopwatch? armedClock = null;

        try
        {
            if (!TryAttach((uint)pid, ct, out var attachError))
                return Fail(primaryCallpoint, pid, attachError);
            attached = true;
            _attachedPid = pid;
            _cleanupProgress = progress;
            if (earlyAttach)
            {
                progress?.Report(new CallpointCaptureStatus(
                    $"已早鸟附加 PID {pid}，等待 {moduleName} 加载…",
                    $"PID {pid}: 已早鸟附加，等待 {moduleName} 加载。"));
            }

            if (!NativeMethods.DebugSetProcessKillOnExit(false))
                return Fail(primaryCallpoint, pid, LastError("DebugSetProcessKillOnExit(false)"));

            _hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION |
                NativeMethods.PROCESS_VM_READ |
                NativeMethods.PROCESS_VM_WRITE |
                NativeMethods.PROCESS_VM_OPERATION,
                false,
                (uint)pid);
            if (_hProcess == IntPtr.Zero)
                return Fail(primaryCallpoint, pid, LastError("OpenProcess"));

            var initialArm = TryArmBreakpoints(
                (uint)pid,
                dllPath,
                moduleName,
                callpoints,
                out var initialArmError,
                out var initialArmedCount);
            if (initialArm == ArmBreakpointsResult.Fatal)
                return Fail(primaryCallpoint, pid, initialArmError);
            if (initialArm == ArmBreakpointsResult.Armed)
            {
                armedClock = Stopwatch.StartNew();
                progress?.Report(new CallpointCaptureStatus(
                    $"{moduleName} 已加载，已同时设置 {initialArmedCount} 个观察点…",
                    $"PID {pid}: {moduleName} 已加载，已同时设置 {initialArmedCount} 个观察点。"));
            }

            var initialBreakpointPending = true;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (shouldStop?.Invoke() == true)
                    return null;
                DebugEvent debugEvent = default;
                if (!NativeMethods.WaitForDebugEvent(ref debugEvent, 100))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == NativeMethods.ERROR_SEM_TIMEOUT)
                    {
                        if (shouldStop?.Invoke() == true)
                            return null;
                        var timeout = CheckCaptureTimeout(
                            primaryCallpoint,
                            pid,
                            moduleName,
                            moduleWaitTimeout,
                            armedCaptureTimeout,
                            moduleClock,
                            armedClock);
                        if (timeout is not null)
                            return timeout;
                        continue;
                    }

                    return Fail(primaryCallpoint, pid, $"WaitForDebugEvent failed: {error}");
                }

                CapturedKeyMaterial? captureResult = null;
                var stopAfterContinue = false;
                var continueStatus = NativeMethods.DBG_CONTINUE;
                var continued = false;
                OperationCanceledException? cancellation = null;
                try
                {
                    var code = (DebugEventCode)debugEvent.dwDebugEventCode;
                    if ((code == DebugEventCode.CreateProcessDebugEvent ||
                         code == DebugEventCode.LoadDllDebugEvent) &&
                        !_bpSet)
                    {
                        var arm = TryArmBreakpoints(
                            (uint)pid,
                            dllPath,
                            moduleName,
                            callpoints,
                            out var breakpointError,
                            out var armedCount);
                        if (arm == ArmBreakpointsResult.Fatal)
                        {
                            captureResult = Fail(primaryCallpoint, pid, breakpointError);
                            stopAfterContinue = true;
                        }
                        else if (arm == ArmBreakpointsResult.Armed)
                        {
                            armedClock = Stopwatch.StartNew();
                            progress?.Report(new CallpointCaptureStatus(
                                $"{moduleName} 已加载，已同时设置 {armedCount} 个观察点…",
                                $"PID {pid}: {moduleName} 已加载，已同时设置 {armedCount} 个观察点。"));
                        }
                    }

                    if (code == DebugEventCode.ExceptionDebugEvent)
                    {
                        var exception = debugEvent.u.Exception.ExceptionRecord;
                        if (exception.ExceptionCode == NativeMethods.EXCEPTION_BREAKPOINT)
                        {
                            if (_bpSet &&
                                TryGetBreakpoint(exception.ExceptionAddress, out var breakpoint) &&
                                exception.ExceptionAddress == _currentBpAddress)
                            {
                                var breakpointResult = HandleBreakpoint(
                                    pid,
                                    debugEvent.dwThreadId,
                                    breakpoint,
                                    acceptCapture,
                                    progress);
                                captureResult = breakpointResult.Capture;
                                stopAfterContinue = breakpointResult.StopAfterContinue;
                                cancellation = breakpointResult.Cancellation;
                                continueStatus = NativeMethods.DBG_CONTINUE;
                            }
                            else if (initialBreakpointPending)
                            {
                                initialBreakpointPending = false;
                                continueStatus = NativeMethods.DBG_CONTINUE;
                            }
                            else
                            {
                                continueStatus = NativeMethods.DBG_EXCEPTION_NOT_HANDLED;
                            }
                        }
                        else if (exception.ExceptionCode == NativeMethods.EXCEPTION_SINGLE_STEP &&
                                 TryCompletePendingRearm(debugEvent.dwThreadId, out var rearmHandled, out var rearmError))
                        {
                            if (!rearmHandled)
                            {
                                continueStatus = NativeMethods.DBG_EXCEPTION_NOT_HANDLED;
                            }
                            else if (rearmError.Length == 0)
                            {
                                continueStatus = NativeMethods.DBG_CONTINUE;
                            }
                            else
                            {
                                captureResult = Fail(primaryCallpoint, pid, rearmError);
                                stopAfterContinue = true;
                                continueStatus = NativeMethods.DBG_CONTINUE;
                            }
                        }
                        else
                        {
                            continueStatus = NativeMethods.DBG_EXCEPTION_NOT_HANDLED;
                        }
                    }
                    else if (code == DebugEventCode.ExitProcessDebugEvent)
                    {
                        captureResult = Fail(primaryCallpoint, pid, "target process exited");
                        stopAfterContinue = true;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    cancellation = ex;
                    stopAfterContinue = true;
                    continueStatus = NativeMethods.DBG_CONTINUE;
                }
                catch (Exception ex)
                {
                    captureResult = Fail(primaryCallpoint, pid, $"debug event handling failed: {ex.Message}");
                    stopAfterContinue = true;
                    continueStatus = NativeMethods.DBG_EXCEPTION_NOT_HANDLED;
                }
                finally
                {
                    CloseDebugEventHandles(debugEvent);
                    continued = NativeMethods.ContinueDebugEvent(
                        debugEvent.dwProcessId,
                        debugEvent.dwThreadId,
                        continueStatus);
                    if (continued) afterContinue?.Invoke();
                }

                if (!continued)
                {
                    captureResult?.Dispose();
                    return Fail(primaryCallpoint, pid, "ContinueDebugEvent failed");
                }

                if (cancellation is not null)
                    throw cancellation;

                if (stopAfterContinue)
                    return captureResult ?? Fail(primaryCallpoint, pid, "breakpoint handling failed");
            }
        }
        finally
        {
            BreakpointRestoreStatus restoreStatus;
            try
            {
                restoreStatus = RestoreBreakpoints();
            }
            finally
            {
                CloseProcessHandle();
                _moduleBase = IntPtr.Zero;
                _currentBpAddress = IntPtr.Zero;
                _pendingRearms.Clear();
                _attachedPid = 0;
                _cleanupProgress = null;
                _workerCancellationToken = default;
            }

            if (attached && restoreStatus is BreakpointRestoreStatus.Restored or BreakpointRestoreStatus.ProcessExited)
                NativeMethods.DebugActiveProcessStop((uint)pid);
            if (restoreStatus == BreakpointRestoreStatus.Fatal)
                throw new BreakpointRestoreException(pid);
        }
    }

    private ArmBreakpointsResult TryArmBreakpoints(
        uint pid,
        string? knownDllPath,
        string moduleName,
        IReadOnlyList<CallpointDefinition> callpoints,
        out string error,
        out int armedCount)
    {
        error = string.Empty;
        armedCount = 0;

        var dllPath = knownDllPath ?? TargetModuleReader.ResolveDllPath(pid, moduleName);
        if (dllPath is null)
            return ArmBreakpointsResult.NotLoaded;

        var requestedNames = callpoints
            .Select(callpoint => callpoint.Name)
            .ToHashSet(StringComparer.Ordinal);
        var inspection = _moduleInspectionCache.Inspect(dllPath, requestedNames);
        var identity = inspection.Identity;
        if (!identity.IsValid || identity.Profile is null)
        {
            error = identity.Error ?? "module identity mismatch";
            return ArmBreakpointsResult.Fatal;
        }

        var profile = identity.Profile;
        var verifiedCallpoints = new List<CallpointDefinition>();
        foreach (var callpoint in profile.Callpoints)
        {
            if (requestedNames.Contains(callpoint.Name) &&
                inspection.VerifiedCallpoints.Contains(callpoint))
            {
                verifiedCallpoints.Add(callpoint);
                if (verifiedCallpoints.Count >= CallpointProfiles.MaxBreakpointsPerAttach)
                    break;
            }
        }
        if (verifiedCallpoints.Count == 0)
        {
            error = "no callpoint file signatures matched";
            return ArmBreakpointsResult.Fatal;
        }

        _moduleBase = ResolveBaseSync(pid, moduleName);
        if (_moduleBase == IntPtr.Zero)
            return ArmBreakpointsResult.NotLoaded;

        if (!SetBreakpoints(verifiedCallpoints, out error))
            return ArmBreakpointsResult.Fatal;

        armedCount = verifiedCallpoints.Count;
        return ArmBreakpointsResult.Armed;
    }

    private CapturedKeyMaterial? CheckCaptureTimeout(
        CallpointDefinition primaryCallpoint,
        int pid,
        string moduleName,
        TimeSpan? moduleWaitTimeout,
        TimeSpan? armedCaptureTimeout,
        Stopwatch moduleClock,
        Stopwatch? armedClock)
    {
        if (!_bpSet && moduleWaitTimeout is TimeSpan moduleTimeout && moduleClock.Elapsed >= moduleTimeout)
        {
            return Fail(
                primaryCallpoint,
                pid,
                $"早鸟等待 {moduleTimeout.TotalSeconds:0} 秒未发现 {moduleName}，请确认已启动目标应用。",
                errorCode: "early-attach:module-timeout");
        }

        if (_bpSet &&
            armedCaptureTimeout is TimeSpan captureTimeout &&
            armedClock is not null &&
            armedClock.Elapsed >= captureTimeout)
        {
            return Fail(
                primaryCallpoint,
                pid,
                $"{captureTimeout.TotalSeconds:0} 秒仍未命中：可能 key 设置早于附加，建议完全退出微信后先点工具再启动。",
                errorCode: "early-attach:capture-timeout");
        }

        return null;
    }

    private BreakpointHitResult HandleBreakpoint(
        int pid,
        uint threadId,
        ActiveBreakpoint breakpoint,
        Func<CapturedKeyMaterial, bool>? acceptCapture,
        IProgress<CallpointCaptureStatus>? progress)
    {
        var callpoint = breakpoint.Definition;
        var hThread = NativeMethods.OpenThread(
            NativeMethods.THREAD_GET_CONTEXT |
            NativeMethods.THREAD_SET_CONTEXT |
            NativeMethods.THREAD_SUSPEND_RESUME |
            NativeMethods.THREAD_QUERY_INFORMATION,
            false,
            threadId);
        if (hThread == IntPtr.Zero)
            return new BreakpointHitResult(Fail(callpoint, pid, LastError("OpenThread")), true);

        var suspended = false;
        try
        {
            var previousSuspendCount = NativeMethods.SuspendThread(hThread);
            if (previousSuspendCount == NativeMethods.INVALID_SUSPEND_COUNT)
                return new BreakpointHitResult(Fail(callpoint, pid, LastError("SuspendThread")), true);
            suspended = true;

            using var contextBuffer = NativeContextBuffer.Create(
                NativeMethods.CONTEXT_CONTROL | NativeMethods.CONTEXT_INTEGER);
            if (!NativeMethods.GetThreadContext(hThread, contextBuffer.Pointer))
            {
                if (RestoreBreakpoint(breakpoint) == BreakpointRestoreStatus.Fatal)
                    return new BreakpointHitResult(
                        Fail(callpoint, pid, "breakpoint_restore_failed"),
                        true);
                return new BreakpointHitResult(Fail(callpoint, pid, LastError("GetThreadContext")), true);
            }

            var ctx = contextBuffer.Read();

            var regs = $"RIP=0x{ctx.Rip:X} RAX=0x{ctx.Rax:X} RCX=0x{ctx.Rcx:X} " +
                $"RDX=0x{ctx.Rdx:X} RSI=0x{ctx.Rsi:X} R8=0x{ctx.R8:X} R9=0x{ctx.R9:X} RSP=0x{ctx.Rsp:X}";

            CapturedKeyMaterial? result = callpoint.Semantics switch
            {
                CallpointRegisterSemantics.Sqlite3KeySink =>
                    ExtractFromRdxR8(ctx, (uint)pid, callpoint, regs),
                CallpointRegisterSemantics.KeyInR8LengthInR9D =>
                    ExtractFromR8R9(ctx, (uint)pid, callpoint, regs),
                CallpointRegisterSemantics.KeyInR9LengthStack5 =>
                    ExtractFromR9Stack5(ctx, (uint)pid, callpoint, regs),
                CallpointRegisterSemantics.BusinessKeyDecoded =>
                    ExtractStringAt((IntPtr)ctx.Rsi, callpoint, regs, pid),
                CallpointRegisterSemantics.BusinessKeyPreEncode =>
                    ExtractStringAt((IntPtr)ctx.R8, callpoint, regs, pid),
                _ => null,
            };

            var keepListening = false;
            OperationCanceledException? cancellation = null;
            if (acceptCapture is not null)
            {
                if (result?.IsValid == true)
                {
                    var candidate = result;
                    try
                    {
                        if (!acceptCapture(candidate))
                        {
                            candidate.Dispose();
                            result = null;
                            keepListening = true;
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        cancellation = ex;
                        candidate.Dispose();
                        result = null;
                    }
                    catch (Exception ex)
                    {
                        candidate.Dispose();
                        result = Fail(callpoint, pid, $"capture validation failed: {ex.Message}", regs);
                    }
                }
                else
                {
                    progress?.Report(new CallpointCaptureStatus(
                        $"{callpoint.Name} 捕获失败，继续监听…",
                        $"PID {pid}: {callpoint.Name}: {result?.Error ?? "捕获结果为空。"}; 继续监听。"));
                    result?.Dispose();
                    result = null;
                    keepListening = true;
                }
            }

            if (RestoreBreakpoint(breakpoint) == BreakpointRestoreStatus.Fatal)
            {
                result?.Dispose();
                return new BreakpointHitResult(
                    Fail(callpoint, pid, "breakpoint_restore_failed"),
                    true);
            }
            ctx.Rip = unchecked((ulong)breakpoint.Address.ToInt64());
            if (keepListening)
            {
                ctx.EFlags |= TrapFlag;
                _pendingRearms[threadId] = breakpoint;
            }
            ctx.ContextFlags = NativeMethods.CONTEXT_CONTROL | NativeMethods.CONTEXT_INTEGER;
            contextBuffer.Write(ctx);
            if (!NativeMethods.SetThreadContext(hThread, contextBuffer.Pointer))
            {
                result?.Dispose();
                return new BreakpointHitResult(Fail(callpoint, pid, LastError("SetThreadContext"), regs), true);
            }

            return new BreakpointHitResult(result, !keepListening, cancellation);
        }
        finally
        {
            if (suspended)
                NativeMethods.ResumeThread(hThread);
            NativeMethods.CloseHandle(hThread);
        }
    }

    private CapturedKeyMaterial? ExtractFromRdxR8(
        ContextAmd64 ctx,
        uint pid,
        CallpointDefinition def,
        string regs) =>
        ReadKeyBytes((IntPtr)ctx.Rdx, (int)(uint)ctx.R8, pid, def, regs, "RDX");

    private CapturedKeyMaterial? ExtractFromR8R9(
        ContextAmd64 ctx,
        uint pid,
        CallpointDefinition def,
        string regs) =>
        ReadKeyBytes((IntPtr)ctx.R8, (int)(uint)ctx.R9, pid, def, regs, "R8");

    private CapturedKeyMaterial? ExtractFromR9Stack5(
        ContextAmd64 ctx,
        uint pid,
        CallpointDefinition def,
        string regs)
    {
        var lengthBytes = new byte[4];
        if (!NativeMethods.ReadProcessMemory(
                _hProcess,
                (IntPtr)(ctx.Rsp + 0x28),
                lengthBytes,
                lengthBytes.Length,
                out var read) || read != lengthBytes.Length)
        {
            return Fail(def, (int)pid, $"ReadMemory at [RSP+0x28]=0x{ctx.Rsp + 0x28:X}", regs);
        }

        var nKey = BitConverter.ToInt32(lengthBytes, 0);
        return ReadKeyBytes((IntPtr)ctx.R9, nKey, pid, def, regs, "R9");
    }

    private CapturedKeyMaterial? ExtractFromSink(
        ContextAmd64 ctx,
        uint pid,
        CallpointDefinition def,
        string regs) =>
        ExtractFromRdxR8(ctx, pid, def, regs);

    private CapturedKeyMaterial? ReadKeyBytes(
        IntPtr keyAddress,
        int nKey,
        uint pid,
        CallpointDefinition def,
        string regs,
        string pointerRegister)
    {
        if (nKey <= 0 || nKey > MaxKeyBytes)
            return Fail(def, (int)pid, $"nKey={nKey}", regs);

        var buffer = new byte[nKey];
        if (!NativeMethods.ReadProcessMemory(
                _hProcess,
                keyAddress,
                buffer,
                nKey,
                out var read) || read != nKey)
        {
            CryptographicOperations.ZeroMemory(buffer);
            return Fail(def, (int)pid, $"ReadMemory at {pointerRegister}=0x{keyAddress.ToInt64():X}", regs);
        }

        return Make(def, regs, (int)pid, buffer, nKey);
    }

    private CapturedKeyMaterial? ExtractStringAt(
        IntPtr strAddr,
        CallpointDefinition def,
        string regs,
        int pid)
    {
        var header = new byte[0x20];
        try
        {
            if (!NativeMethods.ReadProcessMemory(_hProcess, strAddr, header, 0x20, out var headerRead) ||
                headerRead != 0x20)
                return Fail(def, pid, $"string hdr at 0x{strAddr:X}", regs);

            var size = BitConverter.ToInt64(header, 0x10);
            var capacity = BitConverter.ToInt64(header, 0x18);
            if (size <= 0 || size > MaxKeyBytes)
                return Fail(def, pid, $"string size={size}", regs);

            var dataAddr = capacity < 16 ? strAddr : (IntPtr)BitConverter.ToInt64(header, 0);
            var buffer = new byte[size];
            if (!NativeMethods.ReadProcessMemory(
                    _hProcess,
                    dataAddr,
                    buffer,
                    (int)size,
                    out var dataRead) || dataRead != (int)size)
            {
                CryptographicOperations.ZeroMemory(buffer);
                return Fail(def, pid, $"string data at 0x{dataAddr:X}", regs);
            }

            return Make(def, regs, pid, buffer, (int)size);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
        }
    }

    private bool SetBreakpoints(IReadOnlyList<CallpointDefinition> callpoints, out string error)
    {
        error = string.Empty;
        foreach (var callpoint in callpoints)
        {
            if (SetBreakpoint(callpoint, out error))
                continue;

            if (RestoreBreakpoints() == BreakpointRestoreStatus.Fatal)
            {
                error = "breakpoint_restore_failed";
                return false;
            }
            return false;
        }

        return _bpSet;
    }

    private bool SetBreakpoint(CallpointDefinition callpoint, out string error)
    {
        error = string.Empty;
        var breakpointAddress = IntPtr.Add(_moduleBase, callpoint.BreakpointRva);
        if (_breakpoints.Any(breakpoint => breakpoint.Address == breakpointAddress))
            return true;

        var signatureAddress = IntPtr.Add(_moduleBase, callpoint.SignatureRva);
        var remoteSignature = new byte[callpoint.ExpectedSig.Length];
        if (!NativeMethods.ReadProcessMemory(
                _hProcess,
                signatureAddress,
                remoteSignature,
                remoteSignature.Length,
                out var signatureRead) ||
            signatureRead != remoteSignature.Length ||
            !remoteSignature.AsSpan().SequenceEqual(callpoint.ExpectedSig))
        {
            error = "callpoint process signature mismatch";
            return false;
        }

        var original = new byte[1];
        if (!NativeMethods.ReadProcessMemory(_hProcess, breakpointAddress, original, 1, out var originalRead) ||
            originalRead != 1)
        {
            error = "breakpoint byte read failed";
            return false;
        }

        if (!NativeMethods.WriteProcessMemory(
                _hProcess,
                breakpointAddress,
                [0xCC],
                1,
                out var written) || written != 1)
        {
            error = "breakpoint write failed";
            return false;
        }

        NativeMethods.FlushInstructionCache(_hProcess, breakpointAddress, 1);
        _breakpoints.Add(new ActiveBreakpoint(callpoint, breakpointAddress, original[0]));
        return true;
    }

    private bool TryGetBreakpoint(IntPtr address, out ActiveBreakpoint breakpoint)
    {
        foreach (var active in _breakpoints)
        {
            if (active.Address != address)
                continue;

            _currentBpAddress = active.Address;
            breakpoint = active;
            return true;
        }

        breakpoint = null!;
        return false;
    }

    private bool TryCompletePendingRearm(uint threadId, out bool handled, out string error)
    {
        handled = false;
        error = string.Empty;
        if (!_pendingRearms.Remove(threadId, out var breakpoint))
            return false;

        handled = true;
        if (SetBreakpoint(breakpoint.Definition, out var setError))
            return true;

        error = $"breakpoint re-arm failed: {setError}";
        return true;
    }

    private BreakpointRestoreStatus RestoreBreakpoints()
    {
        var status = BreakpointRestoreStatus.Restored;
        var clock = Stopwatch.StartNew();
        var breakpoints = _breakpoints.ToArray();
        for (var index = breakpoints.Length - 1; index >= 0; index--)
        {
            var breakpoint = breakpoints[index];
            var remaining = BreakpointRestoreTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return BreakpointRestoreStatus.Fatal;
            var current = RestoreBreakpointWithStatus(breakpoint, remaining);
            if (current == BreakpointRestoreStatus.Fatal) status = current;
            else if (current == BreakpointRestoreStatus.ProcessExited &&
                     status == BreakpointRestoreStatus.Restored)
                status = current;
        }
        return status;
    }

    private BreakpointRestoreStatus RestoreBreakpoint(ActiveBreakpoint breakpoint) =>
        RestoreBreakpointWithStatus(breakpoint, BreakpointRestoreTimeout);

    private BreakpointRestoreStatus RestoreBreakpointWithStatus(
        ActiveBreakpoint breakpoint,
        TimeSpan timeout)
    {
        if (_hProcess == IntPtr.Zero)
            return BreakpointRestoreStatus.ProcessExited;
        var result = _breakpointRestorer.Restore(
            new BreakpointRestoreRequest(
                _attachedPid,
                _hProcess,
                breakpoint.Address,
                breakpoint.OriginalByte),
            failure => _cleanupProgress?.Report(new CallpointCaptureStatus(
                $"正在恢复 PID {failure.Pid} 的观察点…",
                $"PID {failure.Pid}: 地址 0x{failure.Address:X}; 第 {failure.Attempts} 次恢复尚未通过回读验证，继续重试。")),
            timeout,
            _workerCancellationToken);
        if (result.ProcessHandle != _hProcess)
            _hProcess = result.ProcessHandle;
        if (result.Status is BreakpointRestoreStatus.Restored or BreakpointRestoreStatus.ProcessExited)
        {
            _breakpoints.Remove(breakpoint);
        }
        return result.Status;
    }

    private static void CloseDebugEventHandles(DebugEvent debugEvent)
    {
        switch ((DebugEventCode)debugEvent.dwDebugEventCode)
        {
            case DebugEventCode.CreateThreadDebugEvent:
                CloseIfValid(debugEvent.u.CreateThread.hThread);
                break;
            case DebugEventCode.CreateProcessDebugEvent:
                CloseIfValid(debugEvent.u.CreateProcess.hFile);
                CloseIfValid(debugEvent.u.CreateProcess.hProcess);
                CloseIfValid(debugEvent.u.CreateProcess.hThread);
                break;
            case DebugEventCode.LoadDllDebugEvent:
                CloseIfValid(debugEvent.u.LoadDll.hFile);
                break;
        }

        static void CloseIfValid(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                NativeMethods.CloseHandle(handle);
        }
    }

    private void CloseProcessHandle()
    {
        if (_hProcess == IntPtr.Zero) return;
        NativeMethods.CloseHandle(_hProcess);
        _hProcess = IntPtr.Zero;
    }

    private static IntPtr ResolveBaseSync(uint pid, string moduleName) =>
        TargetModuleReader.ResolveBaseAddress(pid, moduleName) ?? IntPtr.Zero;

    private static bool TryAttach(uint pid, CancellationToken cancellationToken, out string error)
    {
        var attachError = 0;
        for (var attempt = 0; attempt < AttachRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.DebugActiveProcess(pid))
            {
                error = string.Empty;
                return true;
            }

            attachError = Marshal.GetLastPInvokeError();
            NativeMethods.DebugActiveProcessStop(pid);
            if (attempt + 1 < AttachRetryCount &&
                cancellationToken.WaitHandle.WaitOne(AttachRetryDelayMs))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        error = $"DebugActiveProcess failed: {attachError}";
        return false;
    }

    private static string LastError(string api) =>
        $"{api} failed: {Marshal.GetLastPInvokeError()}";

    private static CapturedKeyMaterial Unsupported(CallpointDefinition definition, int pid) =>
        new(
            definition.Name,
            definition.BreakpointRva,
            "non-windows",
            pid,
            DateTime.UtcNow)
        { Error = "DebugCaptureBackend requires Windows." };

    private static CapturedKeyMaterial Fail(
        CallpointDefinition definition,
        int pid,
        string why,
        string? regs = null,
        string? errorCode = null) =>
        new(
            definition.Name,
            definition.BreakpointRva,
            regs ?? $"fail:{why}",
            pid,
            DateTime.UtcNow)
        { Error = errorCode is null ? why : $"{errorCode}: {why}" };

    private static CapturedKeyMaterial Make(
        CallpointDefinition definition,
        string regs,
        int pid,
        byte[] data,
        int length) =>
        new(
            definition.Name,
            definition.BreakpointRva,
            regs,
            pid,
            DateTime.UtcNow)
        {
            KeyData = data,
            KeyLength = length,
        };

    private sealed class NativeContextBuffer : IDisposable
    {
        private const int Alignment = 16;

        private readonly IntPtr _raw;
        private readonly int _size;

        private NativeContextBuffer(IntPtr raw, IntPtr pointer, int size)
        {
            _raw = raw;
            Pointer = pointer;
            _size = size;
        }

        public IntPtr Pointer { get; }

        public static NativeContextBuffer Create(uint flags)
        {
            var size = Marshal.SizeOf<ContextAmd64>();
            var raw = Marshal.AllocHGlobal(size + Alignment - 1);
            var aligned = (raw.ToInt64() + Alignment - 1) & ~((long)Alignment - 1);
            var pointer = new IntPtr(aligned);
            Marshal.StructureToPtr(new ContextAmd64 { ContextFlags = flags }, pointer, false);
            return new NativeContextBuffer(raw, pointer, size);
        }

        public ContextAmd64 Read() => Marshal.PtrToStructure<ContextAmd64>(Pointer);

        public void Write(ContextAmd64 value) => Marshal.StructureToPtr(value, Pointer, false);

        public void Dispose()
        {
            Marshal.Copy(new byte[_size], 0, Pointer, _size);
            Marshal.FreeHGlobal(_raw);
        }
    }

    private sealed record ActiveBreakpoint(
        CallpointDefinition Definition,
        IntPtr Address,
        byte OriginalByte);

    private sealed record BreakpointHitResult(
        CapturedKeyMaterial? Capture,
        bool StopAfterContinue,
        OperationCanceledException? Cancellation = null);

    private enum ArmBreakpointsResult
    {
        NotLoaded,
        Armed,
        Fatal,
    }
}

internal sealed class BreakpointRestoreException : InvalidOperationException
{
    internal BreakpointRestoreException(int pid)
        : base($"breakpoint_restore_failed: PID {pid} remained alive after the restore deadline.")
    {
    }
}
