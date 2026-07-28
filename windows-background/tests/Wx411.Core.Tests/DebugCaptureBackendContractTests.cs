using System.Text.RegularExpressions;
using System.Reflection;
using Wx411.Core.Windows;

namespace Wx411.Core.Tests;

public sealed class DebugCaptureBackendContractTests
{
    [Fact]
    public void FailureCarriesStableCodeInErrorChannel()
    {
        var method = typeof(DebugCaptureBackend).GetMethod(
            "Fail",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Debug capture failure factory was not found.");
        using var failure = (CapturedKeyMaterial)method.Invoke(
            null,
            [CallpointProfiles.Preferred.Callpoints[0], 1, "本地化提示", null, "capture_timeout"]
        )!;

        Assert.Equal("capture_timeout: 本地化提示", failure.Error);
    }

    [Fact]
    public void AttachRetryAcceptsCancellationAndHasNoThreadSleep()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(source, "private static bool TryAttach(", "private static string LastError(");

        Assert.Contains("CancellationToken", method, StringComparison.Ordinal);
        Assert.Contains("ThrowIfCancellationRequested", method, StringComparison.Ordinal);
        Assert.Contains("WaitHandle.WaitOne", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DetachOccursOnlyAfterStructuredBreakpointRestoration()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var cleanup = Slice(source, "finally\n        {", "private ArmBreakpointsResult TryArmBreakpoints(");

        Assert.Contains("BreakpointRestoreStatus", source, StringComparison.Ordinal);
        Assert.True(
            RequiredIndex(cleanup, "RestoreBreakpoints") <
            RequiredIndex(cleanup, "DebugActiveProcessStop"));
    }

    [Fact]
    public void EventLoopPollsTimeoutAndContinuesEveryReceivedEventOnce()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private CapturedKeyMaterial? RunCaptureSync(",
            "private BreakpointHitResult HandleBreakpoint(");

        Assert.Contains("ERROR_SEM_TIMEOUT", method, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(method, @"\bContinueDebugEvent\s*\(").Cast<Match>());
        Assert.Contains("finally", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EventLoopOnlyHandlesItsExactBreakpointAddress()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));

        Assert.Contains("ExceptionAddress", source, StringComparison.Ordinal);
        Assert.Contains("_currentBpAddress", source, StringComparison.Ordinal);
        Assert.Matches(
            @"ExceptionAddress\s*==\s*_currentBpAddress|_currentBpAddress\s*==\s*.*ExceptionAddress",
            source);
    }

    [Fact]
    public void AttachLifecycleProtectsTargetAndDetachesOnlyAfterSafeRestore()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private CapturedKeyMaterial? RunCaptureSync(",
            "private BreakpointHitResult HandleBreakpoint(");

        Assert.Contains("DebugSetProcessKillOnExit(false)", method, StringComparison.Ordinal);
        Assert.Contains("attached = true", method, StringComparison.Ordinal);
        Assert.Contains("restoreStatus", method, StringComparison.Ordinal);
        Assert.Contains("BreakpointRestoreStatus.Restored", method, StringComparison.Ordinal);
        Assert.Contains("BreakpointRestoreStatus.ProcessExited", method, StringComparison.Ordinal);
        Assert.Contains("DebugActiveProcessStop", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachFirstPathSuspendsTargetBeforeModuleInspection()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private CapturedKeyMaterial? RunCaptureSync(",
            "private BreakpointHitResult HandleBreakpoint(");

        var attach = RequiredIndex(method, "TryAttach((uint)pid");
        var arm = RequiredIndex(method, "TryArmBreakpoints(", attach);

        Assert.True(attach < arm);
    }

    [Fact]
    public void BreakpointHandlerReadsRegistersAndRewindsRip()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private BreakpointHitResult HandleBreakpoint(",
            "private CapturedKeyMaterial? ExtractFromSink(");

        Assert.Contains("THREAD_GET_CONTEXT |", method, StringComparison.Ordinal);
        Assert.Contains("THREAD_SET_CONTEXT |", method, StringComparison.Ordinal);
        Assert.Contains("THREAD_SUSPEND_RESUME", method, StringComparison.Ordinal);
        Assert.Contains("SuspendThread", method, StringComparison.Ordinal);
        Assert.Contains("ResumeThread", method, StringComparison.Ordinal);
        Assert.Contains("NativeContextBuffer", method, StringComparison.Ordinal);
        Assert.Contains("CONTEXT_CONTROL | NativeMethods.CONTEXT_INTEGER", method, StringComparison.Ordinal);
        Assert.Contains("ctx.Rip =", method, StringComparison.Ordinal);
        Assert.Contains("SetThreadContext", method, StringComparison.Ordinal);
    }

    [Fact]
    public void BreakpointHandlerExtractsAllKeyRegisterLayouts()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "CapturedKeyMaterial? result = callpoint.Semantics switch",
            "RestoreBreakpoint(breakpoint);");

        Assert.Contains("CallpointRegisterSemantics.Sqlite3KeySink", method, StringComparison.Ordinal);
        Assert.Contains("ExtractFromRdxR8(ctx", method, StringComparison.Ordinal);
        Assert.Contains("CallpointRegisterSemantics.KeyInR8LengthInR9D", method, StringComparison.Ordinal);
        Assert.Contains("ExtractFromR8R9(ctx", method, StringComparison.Ordinal);
        Assert.Contains("CallpointRegisterSemantics.KeyInR9LengthStack5", method, StringComparison.Ordinal);
        Assert.Contains("ExtractFromR9Stack5(ctx", method, StringComparison.Ordinal);
        Assert.Contains("(IntPtr)ctx.Rdx", source, StringComparison.Ordinal);
        Assert.Contains("(IntPtr)ctx.R8", source, StringComparison.Ordinal);
        Assert.Contains("(uint)ctx.R9", source, StringComparison.Ordinal);
        Assert.Contains("ctx.Rsp + 0x28", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachPathRetriesStaleSelfDetachAndReportsWin32Errors()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));

        Assert.Contains("TryAttach", source, StringComparison.Ordinal);
        Assert.Contains("DebugActiveProcessStop", source, StringComparison.Ordinal);
        Assert.Contains("AttachRetryCount = 20", source, StringComparison.Ordinal);
        Assert.Contains("AttachRetryDelayMs = 250", source, StringComparison.Ordinal);
        Assert.Contains("for (var attempt = 0; attempt < AttachRetryCount; attempt++)", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.WaitHandle.WaitOne(AttachRetryDelayMs)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(AttachRetryDelayMs)", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.GetLastPInvokeError()", source, StringComparison.Ordinal);
        Assert.Contains("DebugActiveProcess failed:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyAttachArmsBreakpointsAfterModuleLoadEvents()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var iface = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "ICallpointCaptureBackend.cs"));
        var method = Slice(
            source,
            "public Task<CapturedKeyMaterial?> CaptureAnyWhenModuleLoadsAsync(",
            "private BreakpointHitResult HandleBreakpoint(");

        Assert.Contains("CaptureAnyWhenModuleLoadsAsync", iface, StringComparison.Ordinal);
        Assert.Contains("CallpointCaptureStatus", iface, StringComparison.Ordinal);
        Assert.Contains("LoadDllDebugEvent", method, StringComparison.Ordinal);
        Assert.Contains("CreateProcessDebugEvent", method, StringComparison.Ordinal);
        Assert.Contains("TryArmBreakpoints", method, StringComparison.Ordinal);
        Assert.Contains("已早鸟附加", method, StringComparison.Ordinal);
        Assert.Contains("已同时设置", method, StringComparison.Ordinal);
        Assert.Contains("早鸟等待", method, StringComparison.Ordinal);
        Assert.Contains("秒仍未命中", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyAttachSelectsExactModuleProfileBeforeArming()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private ArmBreakpointsResult TryArmBreakpoints(",
            "private CapturedKeyMaterial? CheckCaptureTimeout(");

        Assert.Contains("identity.Profile", method, StringComparison.Ordinal);
        Assert.Contains("profile.Callpoints", method, StringComparison.Ordinal);
        Assert.Contains("requestedNames", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(callpoint => PeCallpointLocator.VerifySignature", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousValidationRearmsBreakpointAfterRejectedCandidate()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var iface = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "ICallpointCaptureBackend.cs"));
        var handler = Slice(
            source,
            "private BreakpointHitResult HandleBreakpoint(",
            "private CapturedKeyMaterial? ExtractFromSink(");

        Assert.Contains("CaptureAnyUntilAcceptedAsync", iface, StringComparison.Ordinal);
        Assert.Contains("CaptureAnyWhenModuleLoadsUntilAcceptedAsync", iface, StringComparison.Ordinal);
        Assert.Contains("Func<CapturedKeyMaterial, bool>? acceptCapture", source, StringComparison.Ordinal);
        Assert.Contains("if (!acceptCapture(candidate))", handler, StringComparison.Ordinal);
        Assert.Contains("candidate.Dispose();", handler, StringComparison.Ordinal);
        Assert.Contains("ctx.EFlags |= TrapFlag", handler, StringComparison.Ordinal);
        Assert.Contains("_pendingRearms[threadId] = breakpoint", handler, StringComparison.Ordinal);
        Assert.Contains("EXCEPTION_SINGLE_STEP", source, StringComparison.Ordinal);
        Assert.Contains("TryCompletePendingRearm", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingSingleStepIsDrainedBeforeStopCancellationOrTimeout()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var eventLoop = Slice(
            source,
            "private CapturedKeyMaterial? RunCaptureSync(",
            "private bool ShouldStopEventLoop(");
        var stopGuard = Slice(
            source,
            "private bool ShouldStopEventLoop(",
            "private ArmBreakpointsResult TryArmBreakpoints(");

        Assert.Equal(
            2,
            Regex.Matches(eventLoop, @"\bif\s*\(ShouldStopEventLoop\s*\(").Count);
        Assert.DoesNotContain("ct.ThrowIfCancellationRequested();", eventLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("shouldStop?.Invoke()", eventLoop, StringComparison.Ordinal);
        Assert.Contains("_pendingRearms.Count != 0", stopGuard, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", stopGuard, StringComparison.Ordinal);
        Assert.Contains("shouldStop?.Invoke() == true", stopGuard, StringComparison.Ordinal);
        Assert.Contains("if (_pendingRearms.Count != 0)", eventLoop, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendArmsFourDefaultBreakpointsPerAttach()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var profiles = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "CallpointProfile.cs"));
        var method = Slice(
            source,
            "private ArmBreakpointsResult TryArmBreakpoints(",
            "private CapturedKeyMaterial? CheckCaptureTimeout(");

        Assert.Contains("MaxBreakpointsPerAttach = 4", profiles, StringComparison.Ordinal);
        Assert.Contains("verifiedCallpoints.Count >= CallpointProfiles.MaxBreakpointsPerAttach", method, StringComparison.Ordinal);
        Assert.Contains("break;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSignatureIsCheckedBeforeBreakpointWrite()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs"));
        var method = Slice(
            source,
            "private bool SetBreakpoint(",
            "private void RestoreBreakpoint(");

        var signatureCheck = RequiredIndex(method, "ExpectedSig");
        var writeBreakpoint = RequiredIndex(method, "WriteProcessMemory", signatureCheck);
        Assert.True(signatureCheck < writeBreakpoint);
        Assert.Contains("SignatureRva", method, StringComparison.Ordinal);
        Assert.Contains("BreakpointRva", method, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = RequiredIndex(source, startMarker);
        var end = RequiredIndex(source, endMarker, start + startMarker.Length);
        return source[start..end];
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }
}

