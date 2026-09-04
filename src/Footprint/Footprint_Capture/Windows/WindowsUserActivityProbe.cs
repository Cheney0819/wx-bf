using System.Runtime.InteropServices;

namespace Footprint.Capture.Windows;

public interface IUserActivityProbe
{
    TimeSpan GetLastInputAge();
}

[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint LastInputTick;
}

internal interface IWindowsUserActivityNativeApi
{
    bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
}

internal interface IWindowsTickCountClock
{
    ulong GetTickCount64();
}

public sealed class WindowsUserActivityProbe : IUserActivityProbe
{
    private readonly IWindowsUserActivityNativeApi _native;
    private readonly IWindowsTickCountClock _clock;
    private readonly Func<bool> _isWindows;

    public WindowsUserActivityProbe() : this(new PInvokeWindowsUserActivityNativeApi(),
        new EnvironmentTickCountClock(), OperatingSystem.IsWindows)
    { }

    internal WindowsUserActivityProbe(IWindowsUserActivityNativeApi native, IWindowsTickCountClock clock,
        Func<bool> isWindows)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
    }

    public TimeSpan GetLastInputAge()
    {
        if (!_isWindows()) throw new PlatformNotSupportedException("用户空闲检测仅支持 Windows。");

        var lastInputInfo = new LastInputInfo { Size = checked((uint)Marshal.SizeOf<LastInputInfo>()) };
        if (!_native.GetLastInputInfo(ref lastInputInfo))
            throw new InvalidOperationException("无法读取当前会话的用户空闲时间。");

        var currentTick = unchecked((uint)_clock.GetTickCount64());
        var elapsedMilliseconds = unchecked(currentTick - lastInputInfo.LastInputTick);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }
}

internal sealed class EnvironmentTickCountClock : IWindowsTickCountClock
{
    public ulong GetTickCount64() => unchecked((ulong)Environment.TickCount64);
}

internal sealed class PInvokeWindowsUserActivityNativeApi : IWindowsUserActivityNativeApi
{
    public bool GetLastInputInfo(ref LastInputInfo lastInputInfo) => NativeMethods.GetLastInputInfo(ref lastInputInfo);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
    }
}
