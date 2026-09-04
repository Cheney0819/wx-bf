using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Footprint.Capture.Windows;

internal interface IWindowsNativeHandle : IDisposable
{
    bool IsInvalid { get; }
}

internal interface IWindowsNativeFileHandle : IWindowsNativeHandle
{
    Stream Stream { get; }
}

internal interface IWindowsNativeApi
{
    IWindowsNativeHandle CreateToolhelp32Snapshot(uint flags, uint processId, out int errorCode);
    bool Process32First(IWindowsNativeHandle snapshot, ref ProcessEntry32 entry, out int errorCode);
    bool Process32Next(IWindowsNativeHandle snapshot, ref ProcessEntry32 entry, out int errorCode);
    bool Module32First(IWindowsNativeHandle snapshot, ref ModuleEntry32 entry, out int errorCode);
    bool Module32Next(IWindowsNativeHandle snapshot, ref ModuleEntry32 entry, out int errorCode);
    IWindowsNativeHandle OpenProcess(uint access, bool inheritHandle, uint processId, out int errorCode);
    bool QueryFullProcessImageName(IWindowsNativeHandle process, StringBuilder path, ref int size,
        out int errorCode);
    IWindowsNativeFileHandle OpenFileReadOnly(string path, out int errorCode);
    bool GetFinalPathNameByHandle(IWindowsNativeFileHandle file, out string path, out int errorCode);
    bool GetFileIdentity(IWindowsNativeFileHandle file, out WindowsFileIdentity identity, out int errorCode);
    nint GetForegroundWindow();
    uint GetWindowThreadProcessId(nint window, out uint processId);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Win32 ABI field layout")]
internal struct ProcessEntry32
{
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public nuint DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int BasePriority;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Win32 ABI field layout")]
internal struct ModuleEntry32
{
    public uint Size;
    public uint ModuleId;
    public uint ProcessId;
    public uint GlobalUsage;
    public uint ProcessUsage;
    public nint BaseAddress;
    public uint BaseSize;
    public nint ModuleHandle;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ModuleName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutablePath;
}

internal sealed class ToolhelpWindowsProcessApi : IWindowsProcessApi
{
    internal const uint SnapshotProcesses = 0x00000002;
    internal const uint SnapshotModules = 0x00000008;
    internal const uint SnapshotModules32 = 0x00000010;
    internal const uint QueryLimitedInformation = 0x00001000;
    internal const int ErrorNoMoreFiles = 18;
    internal const int ErrorBadLength = 24;
    internal const int ErrorInsufficientBuffer = 122;
    private readonly IWindowsNativeApi _native;

    public ToolhelpWindowsProcessApi() : this(new PInvokeWindowsNativeApi()) { }

    internal ToolhelpWindowsProcessApi(IWindowsNativeApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public IReadOnlyList<WindowsProcessEntry> EnumerateProcesses()
    {
        using var snapshot = _native.CreateToolhelp32Snapshot(SnapshotProcesses, 0, out _);
        if (snapshot.IsInvalid) throw ObservationFailure("process_enumeration_failed");

        var entry = NewProcessEntry();
        var processes = new List<WindowsProcessEntry>();
        if (!_native.Process32First(snapshot, ref entry, out var error))
        {
            if (error == ErrorNoMoreFiles) return processes;
            throw ObservationFailure("process_enumeration_failed");
        }

        while (true)
        {
            processes.Add(new WindowsProcessEntry(checked((int)entry.ProcessId), entry.ExecutableFile));
            entry = NewProcessEntry();
            if (_native.Process32Next(snapshot, ref entry, out error)) continue;
            if (error != ErrorNoMoreFiles) throw ObservationFailure("process_enumeration_failed");
            return processes;
        }
    }

    public string QueryExecutablePath(int processId)
    {
        using var process = _native.OpenProcess(QueryLimitedInformation, false, checked((uint)processId), out _);
        if (process.IsInvalid) throw ObservationFailure("process_inaccessible", processId);

        var capacity = 260;
        while (capacity <= 32768)
        {
            var path = new StringBuilder(capacity);
            var size = capacity;
            if (_native.QueryFullProcessImageName(process, path, ref size, out var error)) return path.ToString();
            if (error != ErrorInsufficientBuffer) throw ObservationFailure("process_inaccessible", processId);
            capacity = Math.Max(capacity * 2, size);
        }
        throw ObservationFailure("process_inaccessible", processId);
    }

    public IStableFileCapture OpenStableFile(string path) => OpenStableFileCore(path, 0);

    public StableLoadedModuleCapture? CaptureLoadedModule(int processId, string moduleName)
    {
        var opened = OpenMatchingModule(processId, moduleName);
        if (opened is null) return null;
        var (first, stableFile) = opened.Value;
        try
        {
            if (!WindowsWeixinProbe.CanonicalPathsEqual(stableFile.CanonicalPath, first.ExecutablePath))
                throw ObservationFailure("module_snapshot_changed", processId);

            var second = FindModule(processId, moduleName);
            if (second is null || second.BaseAddress != first.BaseAddress ||
                !WindowsWeixinProbe.CanonicalPathsEqual(second.ExecutablePath, first.ExecutablePath))
                throw ObservationFailure("module_snapshot_changed", processId);

            return new StableLoadedModuleCapture(stableFile, first.BaseAddress);
        }
        catch
        {
            stableFile.Dispose();
            throw;
        }
    }

    public int? GetForegroundProcessId()
    {
        var window = _native.GetForegroundWindow();
        if (window == 0) return null;
        _ = _native.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : checked((int)processId);
    }

    private NativeStableFileCapture OpenStableFileCore(string path, int processId)
    {
        var handle = _native.OpenFileReadOnly(path, out _);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw ObservationFailure("file_snapshot_unavailable", processId);
        }

        try
        {
            if (!_native.GetFinalPathNameByHandle(handle, out var canonicalPath, out _) ||
                string.IsNullOrWhiteSpace(canonicalPath) ||
                !_native.GetFileIdentity(handle, out var identity, out _))
                throw ObservationFailure("file_snapshot_unavailable", processId);
            return new NativeStableFileCapture(handle,
                WindowsWeixinProbe.NormalizeCanonicalPath(canonicalPath), identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private IReadOnlyList<ModuleRow> EnumerateModules(int processId)
    {
        using var snapshot = CreateModuleSnapshot(processId);
        var entry = NewModuleEntry();
        var modules = new List<ModuleRow>();
        if (!_native.Module32First(snapshot, ref entry, out var error))
        {
            if (error == ErrorNoMoreFiles) return modules;
            throw ObservationFailure("process_inaccessible", processId);
        }

        while (true)
        {
            modules.Add(new ModuleRow(entry.ModuleName, entry.ExecutablePath, entry.BaseAddress));
            entry = NewModuleEntry();
            if (_native.Module32Next(snapshot, ref entry, out error)) continue;
            if (error != ErrorNoMoreFiles) throw ObservationFailure("process_inaccessible", processId);
            return modules;
        }
    }

    private (ModuleRow Row, NativeStableFileCapture File)? OpenMatchingModule(int processId, string moduleName)
    {
        using var snapshot = CreateModuleSnapshot(processId);
        var entry = NewModuleEntry();
        if (!_native.Module32First(snapshot, ref entry, out var error))
        {
            if (error == ErrorNoMoreFiles) return null;
            throw ObservationFailure("process_inaccessible", processId);
        }

        while (true)
        {
            var row = new ModuleRow(entry.ModuleName, entry.ExecutablePath, entry.BaseAddress);
            if (IsModuleMatch(row, moduleName))
                return (row, OpenStableFileCore(row.ExecutablePath, processId));
            entry = NewModuleEntry();
            if (_native.Module32Next(snapshot, ref entry, out error)) continue;
            if (error == ErrorNoMoreFiles) return null;
            throw ObservationFailure("process_inaccessible", processId);
        }
    }

    private ModuleRow? FindModule(int processId, string moduleName) =>
        EnumerateModules(processId).FirstOrDefault(module => IsModuleMatch(module, moduleName));

    private static bool IsModuleMatch(ModuleRow module, string moduleName) =>
        string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(module.ExecutablePath), moduleName, StringComparison.OrdinalIgnoreCase);

    private IWindowsNativeHandle CreateModuleSnapshot(int processId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = _native.CreateToolhelp32Snapshot(SnapshotModules | SnapshotModules32,
                checked((uint)processId), out var error);
            if (!snapshot.IsInvalid) return snapshot;
            snapshot.Dispose();
            if (error != ErrorBadLength) break;
        }
        throw ObservationFailure("process_inaccessible", processId);
    }

    private static ProcessEntry32 NewProcessEntry() => new()
    {
        Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
        ExecutableFile = string.Empty
    };

    private static ModuleEntry32 NewModuleEntry() => new()
    {
        Size = (uint)Marshal.SizeOf<ModuleEntry32>(),
        ModuleName = string.Empty,
        ExecutablePath = string.Empty
    };

    private static WindowsObservationException ObservationFailure(string code, int processId = 0) =>
        new(code, processId);

    private sealed record ModuleRow(string ModuleName, string ExecutablePath, nint BaseAddress);
}

internal sealed class NativeStableFileCapture(
    IWindowsNativeFileHandle handle,
    string canonicalPath,
    WindowsFileIdentity identity) : IStableFileCapture
{
    private bool _disposed;
    public string CanonicalPath { get; } = canonicalPath;
    public WindowsFileIdentity Identity { get; } = identity;

    public async Task<string> HashStableCopyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"footprint-stable-file-{Guid.NewGuid():N}.tmp");
            var source = handle.Stream;
            source.Position = 0;
            await using var copy = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            await source.CopyToAsync(copy, cancellationToken);
            await copy.FlushAsync(cancellationToken);
            copy.Position = 0;
            return Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken)).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new WindowsObservationException("file_snapshot_unavailable");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        handle.Dispose();
    }
}

internal sealed class PInvokeWindowsNativeApi : IWindowsNativeApi
{
    public IWindowsNativeHandle CreateToolhelp32Snapshot(uint flags, uint processId, out int errorCode)
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(flags, processId);
        errorCode = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return new NativeHandle(handle);
    }

    public bool Process32First(IWindowsNativeHandle snapshot, ref ProcessEntry32 entry, out int errorCode) =>
        InvokeSnapshot(NativeMethods.Process32First, snapshot, ref entry, out errorCode);

    public bool Process32Next(IWindowsNativeHandle snapshot, ref ProcessEntry32 entry, out int errorCode) =>
        InvokeSnapshot(NativeMethods.Process32Next, snapshot, ref entry, out errorCode);

    public bool Module32First(IWindowsNativeHandle snapshot, ref ModuleEntry32 entry, out int errorCode) =>
        InvokeSnapshot(NativeMethods.Module32First, snapshot, ref entry, out errorCode);

    public bool Module32Next(IWindowsNativeHandle snapshot, ref ModuleEntry32 entry, out int errorCode) =>
        InvokeSnapshot(NativeMethods.Module32Next, snapshot, ref entry, out errorCode);

    public IWindowsNativeHandle OpenProcess(uint access, bool inheritHandle, uint processId, out int errorCode)
    {
        var handle = NativeMethods.OpenProcess(access, inheritHandle, processId);
        errorCode = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return new NativeHandle(handle);
    }

    public bool QueryFullProcessImageName(IWindowsNativeHandle process, StringBuilder path, ref int size,
        out int errorCode)
    {
        var result = NativeMethods.QueryFullProcessImageName(Handle(process), 0, path, ref size);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    public IWindowsNativeFileHandle OpenFileReadOnly(string path, out int errorCode)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            errorCode = 0;
            return new NativeFileHandle(stream);
        }
        catch (Exception)
        {
            errorCode = Marshal.GetLastWin32Error();
            return new InvalidNativeFileHandle();
        }
    }

    public bool GetFinalPathNameByHandle(IWindowsNativeFileHandle file, out string path, out int errorCode)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = NativeMethods.GetFinalPathNameByHandle(FileHandle(file), buffer, (uint)capacity, 0);
            if (length == 0)
            {
                path = string.Empty;
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
            if (length < capacity)
            {
                path = buffer.ToString();
                errorCode = 0;
                return true;
            }
            capacity = checked((int)length + 1);
        }
        path = string.Empty;
        errorCode = ToolhelpWindowsProcessApi.ErrorInsufficientBuffer;
        return false;
    }

    public bool GetFileIdentity(IWindowsNativeFileHandle file, out WindowsFileIdentity identity, out int errorCode)
    {
        if (NativeMethods.GetFileInformationByHandleEx(FileHandle(file), FileInfoByHandleClass.FileIdInfo,
                out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            identity = new WindowsFileIdentity(info.VolumeSerialNumber, info.FileId.Low, info.FileId.High);
            errorCode = 0;
            return true;
        }
        identity = new WindowsFileIdentity(0, 0, 0);
        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public uint GetWindowThreadProcessId(nint window, out uint processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out processId);

    private static bool InvokeSnapshot<T>(SnapshotCall<T> call, IWindowsNativeHandle snapshot, ref T entry,
        out int errorCode) where T : struct
    {
        var result = call(Handle(snapshot), ref entry);
        errorCode = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    private static SafeHandle Handle(IWindowsNativeHandle handle) =>
        handle is NativeHandle native ? native.Handle : throw new InvalidOperationException("原生句柄类型无效。");

    private static SafeFileHandle FileHandle(IWindowsNativeFileHandle handle) =>
        handle is NativeFileHandle native ? native.Stream.SafeFileHandle :
        throw new InvalidOperationException("原生文件句柄类型无效。");

    private delegate bool SnapshotCall<T>(SafeHandle snapshot, ref T entry) where T : struct;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    private sealed class NativeHandle(SafeHandle handle) : IWindowsNativeHandle
    {
        public SafeHandle Handle { get; } = handle;
        public bool IsInvalid => Handle.IsInvalid;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class NativeFileHandle(FileStream stream) : IWindowsNativeFileHandle
    {
        public FileStream Stream { get; } = stream;
        Stream IWindowsNativeFileHandle.Stream => Stream;
        public bool IsInvalid => Stream.SafeFileHandle.IsInvalid;
        public void Dispose() => Stream.Dispose();
    }

    private sealed class InvalidNativeFileHandle : IWindowsNativeFileHandle
    {
        public bool IsInvalid => true;
        public Stream Stream => Stream.Null;
        public void Dispose() { }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(SafeHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(SafeHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Module32First(SafeHandle snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Module32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Module32Next(SafeHandle snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeHandle process, uint flags,
            StringBuilder executablePath, ref int size);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path,
            uint pathLength, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass, out FileIdInfo fileInformation, uint bufferSize);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
