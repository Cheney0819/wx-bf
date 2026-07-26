using Wx411.Core.Windows;

namespace Wx411.Core.Tests;

public sealed class BreakpointRestorerTests
{
    [Fact]
    public void RestoreWritesFlushesAndReadsBackInOrder()
    {
        var operations = new FakeOperations();
        var restorer = new BreakpointRestorer(operations);

        var result = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            null,
            CancellationToken.None);

        Assert.Equal(BreakpointRestoreStatus.Restored, result.Status);
        Assert.Equal(["write:10", "flush:10", "read:10"], operations.Events);
        Assert.Equal((nint)10, result.ProcessHandle);
    }

    [Fact]
    public void RestoreReopensHandleAfterFailedVerification()
    {
        var operations = new FakeOperations { FirstReadValue = 0xCC };
        var restorer = new BreakpointRestorer(operations);

        var result = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            null,
            CancellationToken.None);

        Assert.Equal(BreakpointRestoreStatus.Restored, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal((nint)11, result.ProcessHandle);
        Assert.Contains("reopen", operations.Events);
        Assert.Contains("close:10", operations.Events);
    }

    [Fact]
    public void ExitedProcessNeedsNoFurtherRestore()
    {
        var operations = new FakeOperations { WriteSucceeds = false, IsAlive = false };
        var restorer = new BreakpointRestorer(operations);

        var result = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            null,
            CancellationToken.None);

        Assert.Equal(BreakpointRestoreStatus.ProcessExited, result.Status);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void FailedLiveAttemptReportsFatalStateBeforeRetry()
    {
        var operations = new FakeOperations { FirstReadValue = 0xCC };
        var states = new List<BreakpointRestoreResult>();
        var restorer = new BreakpointRestorer(operations);

        var final = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            states.Add,
            CancellationToken.None);

        Assert.Contains(states, state => state.Status == BreakpointRestoreStatus.Fatal);
        Assert.Equal(BreakpointRestoreStatus.Restored, final.Status);
    }

    [Fact]
    public void LiveProcessStopsRetryingAtRestoreDeadline()
    {
        var operations = new FakeOperations
        {
            AlwaysReadWrongByte = true,
            DelayDuration = TimeSpan.FromMilliseconds(10),
        };
        var restorer = new BreakpointRestorer(operations);

        var result = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            null,
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None);

        Assert.Equal(BreakpointRestoreStatus.Fatal, result.Status);
        Assert.InRange(result.Attempts, 1, 2);
        Assert.True(operations.IsAlive);
        Assert.Contains("deadline", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreCancelledCleanupStillMakesOneRestoreAttemptAndReturnsFatal()
    {
        var operations = new FakeOperations { AlwaysReadWrongByte = true };
        var restorer = new BreakpointRestorer(operations);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = restorer.Restore(
            new BreakpointRestoreRequest(7, (nint)10, (nint)20, 0x48),
            null,
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.Equal(BreakpointRestoreStatus.Fatal, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("cancel", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["write:10", "flush:10", "read:10"], operations.Events[..3]);
    }

    private sealed class FakeOperations : IBreakpointRestoreOperations
    {
        private int _reads;

        internal List<string> Events { get; } = [];
        internal bool WriteSucceeds { get; init; } = true;
        internal bool IsAlive { get; init; } = true;
        internal byte FirstReadValue { get; init; } = 0x48;
        internal bool AlwaysReadWrongByte { get; init; }
        internal TimeSpan DelayDuration { get; init; }

        public bool WriteByte(nint processHandle, nint address, byte value)
        {
            Events.Add($"write:{processHandle}");
            return WriteSucceeds;
        }

        public bool FlushInstructionCache(nint processHandle, nint address)
        {
            Events.Add($"flush:{processHandle}");
            return true;
        }

        public bool ReadByte(nint processHandle, nint address, out byte value)
        {
            Events.Add($"read:{processHandle}");
            value = AlwaysReadWrongByte
                ? (byte)0xCC
                : _reads++ == 0 ? FirstReadValue : (byte)0x48;
            return true;
        }

        public bool IsProcessAlive(int pid) => IsAlive;

        public nint ReopenProcess(int pid)
        {
            Events.Add("reopen");
            return (nint)11;
        }

        public void CloseHandle(nint handle) => Events.Add($"close:{handle}");

        public void Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DelayDuration > TimeSpan.Zero) Thread.Sleep(DelayDuration);
        }
    }
}
