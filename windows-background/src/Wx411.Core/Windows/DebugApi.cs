using System.Runtime.InteropServices;

namespace Wx411.Core.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct ModuleEntry32
{
    public uint dwSize;
    public uint th32ModuleID;
    public uint th32ProcessID;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public IntPtr modBaseAddr;
    public uint modBaseSize;
    public IntPtr hModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExePath;
}

[StructLayout(LayoutKind.Explicit, Size = 0x4D0)]
internal struct ContextAmd64
{
    [FieldOffset(0x30)] public uint ContextFlags;
    [FieldOffset(0x34)] public uint MxCsr;
    [FieldOffset(0x44)] public uint EFlags;
    [FieldOffset(0x78)] public ulong Rax;
    [FieldOffset(0x80)] public ulong Rcx;
    [FieldOffset(0x88)] public ulong Rdx;
    [FieldOffset(0x90)] public ulong Rbx;
    [FieldOffset(0x98)] public ulong Rsp;
    [FieldOffset(0xA0)] public ulong Rbp;
    [FieldOffset(0xA8)] public ulong Rsi;
    [FieldOffset(0xB0)] public ulong Rdi;
    [FieldOffset(0xB8)] public ulong R8;
    [FieldOffset(0xC0)] public ulong R9;
    [FieldOffset(0xC8)] public ulong R10;
    [FieldOffset(0xD0)] public ulong R11;
    [FieldOffset(0xD8)] public ulong R12;
    [FieldOffset(0xE0)] public ulong R13;
    [FieldOffset(0xE8)] public ulong R14;
    [FieldOffset(0xF0)] public ulong R15;
    [FieldOffset(0xF8)] public ulong Rip;
}

[StructLayout(LayoutKind.Explicit, Size = 0x98)]
internal struct ExceptionRecord
{
    [FieldOffset(0x00)] public uint ExceptionCode;
    [FieldOffset(0x04)] public uint ExceptionFlags;
    [FieldOffset(0x08)] public IntPtr ExceptionRecordPtr;
    [FieldOffset(0x10)] public IntPtr ExceptionAddress;
    [FieldOffset(0x18)] public uint NumberParameters;
    [FieldOffset(0x20)] public ulong ExceptionInformation0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ExceptionDebugInfo
{
    public ExceptionRecord ExceptionRecord;
    public uint dwFirstChance;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CreateThreadDebugInfo
{
    public IntPtr hThread;
    public IntPtr lpThreadLocalBase;
    public IntPtr lpStartAddress;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CreateProcessDebugInfo
{
    public IntPtr hFile;
    public IntPtr hProcess;
    public IntPtr hThread;
    public IntPtr lpBaseOfImage;
    public uint dwDebugInfoFileOffset;
    public uint nDebugInfoSize;
    public IntPtr lpThreadLocalBase;
    public IntPtr lpStartAddress;
    public IntPtr lpImageName;
    public ushort fUnicode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LoadDllDebugInfo
{
    public IntPtr hFile;
    public IntPtr lpBaseOfDll;
    public uint dwDebugInfoFileOffset;
    public uint nDebugInfoSize;
    public IntPtr lpImageName;
    public ushort fUnicode;
}

[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
internal struct DebugEventUnion
{
    [FieldOffset(0)] public ExceptionDebugInfo Exception;
    [FieldOffset(0)] public CreateThreadDebugInfo CreateThread;
    [FieldOffset(0)] public CreateProcessDebugInfo CreateProcess;
    [FieldOffset(0)] public LoadDllDebugInfo LoadDll;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DebugEvent
{
    public uint dwDebugEventCode;
    public uint dwProcessId;
    public uint dwThreadId;
    public DebugEventUnion u;
}

internal enum DebugEventCode : uint
{
    ExceptionDebugEvent = 1,
    CreateThreadDebugEvent = 2,
    CreateProcessDebugEvent = 3,
    ExitThreadDebugEvent = 4,
    ExitProcessDebugEvent = 5,
    LoadDllDebugEvent = 6,
    UnloadDllDebugEvent = 7,
    OutputDebugStringEvent = 8,
    RipEvent = 9,
}

internal static class NativeMethods
{
    internal const uint EXCEPTION_BREAKPOINT = 0x80000003;
    internal const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    internal const uint DBG_CONTINUE = 0x00010002;
    internal const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    internal const uint CONTEXT_AMD64 = 0x00100000;
    internal const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x00000001;
    internal const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x00000002;
    internal const uint THREAD_SUSPEND_RESUME = 0x0002;
    internal const uint THREAD_QUERY_INFORMATION = 0x0040;
    internal const uint THREAD_GET_CONTEXT = 0x0008;
    internal const uint THREAD_SET_CONTEXT = 0x0010;
    internal const uint TH32CS_SNAPMODULE = 0x00000008;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_VM_READ = 0x0010;
    internal const uint PROCESS_VM_WRITE = 0x0020;
    internal const uint PROCESS_VM_OPERATION = 0x0008;
    internal const uint INVALID_SUSPEND_COUNT = 0xFFFFFFFF;
    internal const int ERROR_SEM_TIMEOUT = 121;

    private const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32First(IntPtr hSnapshot, ref ModuleEntry32 lpme);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32Next(IntPtr hSnapshot, ref ModuleEntry32 lpme);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcess(uint dwProcessId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcessStop(uint dwProcessId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugSetProcessKillOnExit(
        [MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WaitForDebugEvent(ref DebugEvent lpDebugEvent, uint dwMilliseconds);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ContinueDebugEvent(
        uint dwProcessId,
        uint dwThreadId,
        uint dwContinueStatus);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadContext(IntPtr hThread, ref ContextAmd64 lpContext);

    [DllImport(Kernel32, EntryPoint = "GetThreadContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadContext(IntPtr hThread, ref ContextAmd64 lpContext);

    [DllImport(Kernel32, EntryPoint = "SetThreadContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern uint SuspendThread(IntPtr hThread);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr hThread);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesWritten);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        int dwSize);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr OpenThread(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwThreadId);
}
