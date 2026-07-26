using System.Diagnostics;

namespace Wx411.Core.Windows;

internal enum BreakpointRestoreStatus
{
    Restored,
    ProcessExited,
    Fatal,
}

internal sealed record BreakpointRestoreRequest(
    int Pid,
    nint ProcessHandle,
    nint Address,
    byte OriginalByte);

internal sealed record BreakpointRestoreResult(
    BreakpointRestoreStatus Status,
    int Pid,
    nint Address,
    int Attempts,
    nint ProcessHandle,
    string? Error);

internal interface IBreakpointRestoreOperations
{
    bool WriteByte(nint processHandle, nint address, byte value);
    bool FlushInstructionCache(nint processHandle, nint address);
    bool ReadByte(nint processHandle, nint address, out byte value);
    bool IsProcessAlive(int pid);
    nint ReopenProcess(int pid);
    void CloseHandle(nint handle);
    void Delay(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class BreakpointRestorer
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly IBreakpointRestoreOperations _operations;

    internal BreakpointRestorer(IBreakpointRestoreOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
    }

    internal BreakpointRestoreResult Restore(
        BreakpointRestoreRequest request,
        Action<BreakpointRestoreResult>? report,
        CancellationToken cleanupToken)
    {
        var processHandle = request.ProcessHandle;
        for (var attempt = 1; ; attempt++)
        {
            cleanupToken.ThrowIfCancellationRequested();
            if (_operations.WriteByte(processHandle, request.Address, request.OriginalByte) &&
                _operations.FlushInstructionCache(processHandle, request.Address) &&
                _operations.ReadByte(processHandle, request.Address, out var actual) &&
                actual == request.OriginalByte)
            {
                return new BreakpointRestoreResult(
                    BreakpointRestoreStatus.Restored,
                    request.Pid,
                    request.Address,
                    attempt,
                    processHandle,
                    null);
            }

            if (!_operations.IsProcessAlive(request.Pid))
            {
                return new BreakpointRestoreResult(
                    BreakpointRestoreStatus.ProcessExited,
                    request.Pid,
                    request.Address,
                    attempt,
                    processHandle,
                    null);
            }

            var failure = new BreakpointRestoreResult(
                BreakpointRestoreStatus.Fatal,
                request.Pid,
                request.Address,
                attempt,
                processHandle,
                "Original breakpoint byte could not be verified; retrying while the process is alive.");
            report?.Invoke(failure);

            var reopened = _operations.ReopenProcess(request.Pid);
            if (reopened != 0)
            {
                _operations.CloseHandle(processHandle);
                processHandle = reopened;
            }
            _operations.Delay(RetryDelay, cleanupToken);
        }
    }
}

internal sealed class NativeBreakpointRestoreOperations : IBreakpointRestoreOperations
{
    private const uint ProcessAccess =
        NativeMethods.PROCESS_QUERY_INFORMATION |
        NativeMethods.PROCESS_VM_READ |
        NativeMethods.PROCESS_VM_WRITE |
        NativeMethods.PROCESS_VM_OPERATION;

    public bool WriteByte(nint processHandle, nint address, byte value) =>
        NativeMethods.WriteProcessMemory(processHandle, address, [value], 1, out var written) &&
        written == 1;

    public bool FlushInstructionCache(nint processHandle, nint address) =>
        NativeMethods.FlushInstructionCache(processHandle, address, 1);

    public bool ReadByte(nint processHandle, nint address, out byte value)
    {
        var buffer = new byte[1];
        var success = NativeMethods.ReadProcessMemory(
            processHandle,
            address,
            buffer,
            1,
            out var read) && read == 1;
        value = buffer[0];
        return success;
    }

    public bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    public nint ReopenProcess(int pid) =>
        NativeMethods.OpenProcess(ProcessAccess, false, (uint)pid);

    public void CloseHandle(nint handle)
    {
        if (handle != 0) NativeMethods.CloseHandle(handle);
    }

    public void Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken).GetAwaiter().GetResult();
}
