using System.Runtime.InteropServices;
using System.Text;

namespace Wx411.Core.Windows;

public static class ProcessFileHandleFinder
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameTimeout = 150;
    private const uint StatusSuccess = 0;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;
    private const uint PROCESS_DUP_HANDLE = 0x00000040;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
    private const uint FILE_TYPE_DISK = 0x00000001;
    private const uint FILE_NAME_NORMALIZED = 0x00000000;
    private const uint VOLUME_NAME_DOS = 0x00000000;
    private static readonly Lazy<BoundedHandlePathQueryExecutor> PathQueryExecutor = new(
        () => new BoundedHandlePathQueryExecutor(ReadFinalPathName, handle => CloseHandle(handle)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<int> FindProcessIdsHoldingFile(
        string path,
        IReadOnlyCollection<int>? candidatePids = null)
    {
        if (candidatePids is { Count: 0 })
            return Array.Empty<int>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<int>();
        }

        var targetPath = NormalizeDosPath(path);
        if (targetPath.Length == 0)
            return Array.Empty<int>();

        var candidates = candidatePids is null
            ? null
            : new HashSet<int>(candidatePids.Where(pid => pid > 0));
        if (candidates is { Count: 0 })
            return Array.Empty<int>();

        var processHandles = new Dictionary<int, nint>();
        var result = new List<int>();
        var seen = new HashSet<int>();
        nint buffer = 0;

        try
        {
            if (!TryQuerySystemHandles(out buffer, out var handleCount))
                return result;

            var entrySize = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
            var entryAddress = nint.Add(buffer, nint.Size * 2);
            for (long index = 0; index < handleCount; index++)
            {
                var entry = Marshal.PtrToStructure<SystemHandleTableEntryInfoEx>(
                    nint.Add(entryAddress, checked((int)(index * entrySize))));
                var pid = ToPositiveInt(entry.UniqueProcessId);
                if (pid is null ||
                    seen.Contains(pid.Value) ||
                    candidates is not null && !candidates.Contains(pid.Value))
                {
                    continue;
                }

                if (!TryGetProcessHandle(pid.Value, processHandles, out var processHandle))
                    continue;

                var sourceHandle = new nint(unchecked((long)entry.HandleValue.ToUInt64()));
                if (!DuplicateHandle(
                        processHandle,
                        sourceHandle,
                        GetCurrentProcess(),
                        out var duplicatedHandle,
                        0,
                        false,
                        DUPLICATE_SAME_ACCESS))
                {
                    continue;
                }

                if (GetFileType(duplicatedHandle) != FILE_TYPE_DISK)
                {
                    CloseHandle(duplicatedHandle);
                    continue;
                }

                var pathQuery = PathQueryExecutor.Value.TryQuery(
                    duplicatedHandle,
                    TimeSpan.FromMilliseconds(ObjectNameTimeout));
                if (pathQuery.Status == HandlePathQueryStatus.TimedOut)
                    break;
                if (pathQuery.Status != HandlePathQueryStatus.Success)
                    continue;

                if (!NormalizeDosPath(pathQuery.Path).Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(pid.Value);
                seen.Add(pid.Value);
            }

            return result;
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return result;
        }
        finally
        {
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);

            foreach (var handle in processHandles.Values)
                CloseHandle(handle);
        }
    }

    private static bool TryQuerySystemHandles(out nint buffer, out long handleCount)
    {
        var length = 1024 * 1024;
        buffer = 0;
        handleCount = 0;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            buffer = Marshal.AllocHGlobal(length);
            var status = NtQuerySystemInformation(
                SystemExtendedHandleInformation,
                buffer,
                length,
                out var returnedLength);

            if (status == StatusSuccess)
            {
                handleCount = Marshal.ReadIntPtr(buffer).ToInt64();
                return handleCount >= 0;
            }

            Marshal.FreeHGlobal(buffer);
            buffer = 0;

            if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall)
                return false;

            length = Math.Max(length * 2, returnedLength + 64 * 1024);
        }

        return false;
    }

    private static bool TryGetProcessHandle(
        int pid,
        Dictionary<int, nint> processHandles,
        out nint processHandle)
    {
        if (processHandles.TryGetValue(pid, out processHandle))
            return processHandle != 0;

        processHandle = OpenProcess(PROCESS_DUP_HANDLE, false, (uint)pid);
        processHandles.Add(pid, processHandle);
        return processHandle != 0;
    }

    private static string ReadFinalPathName(nint handle)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                builder,
                (uint)builder.Capacity,
                FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);

            if (length == 0)
                return string.Empty;
            if (length < builder.Capacity)
                return builder.ToString(0, (int)length);

            capacity = checked((int)length + 1);
        }

        return string.Empty;
    }

    private static int? ToPositiveInt(UIntPtr value)
    {
        var raw = value.ToUInt64();
        return raw is > 0 and <= int.MaxValue ? (int)raw : null;
    }

    private static string NormalizeDosPath(string path)
    {
        var normalized = path.Trim().TrimEnd('\0').Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            normalized = @"\\" + normalized[8..];
        else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return string.Empty;
        }

        return normalized.TrimEnd('\\');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfoEx
    {
        public nint Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        nint systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        nint sourceProcessHandle,
        nint sourceHandle,
        nint targetProcessHandle,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        nint file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
