using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopPet.Background.Contracts;

namespace DesktopPet.Recovery;

public sealed record WeChatRuntimeIdentity(
    int ProcessId,
    int SessionId,
    string ExecutablePath,
    string ExecutableIdentity,
    RecoveryEpochIdentity? EpochIdentity = null,
    string? DataRoot = null);

public sealed class AmbiguousWeChatProcessException : InvalidOperationException
{
    public AmbiguousWeChatProcessException(int candidateCount)
        : base("Multiple target processes are active in the worker session.")
    {
        if (candidateCount < 2)
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        CandidateCount = candidateCount;
    }

    public int CandidateCount { get; }

    public string Code => "ambiguous_wechat_process";
}

internal sealed record WeChatProcessCandidate(
    int ProcessId,
    int SessionId,
    string ExecutablePath,
    bool HasMainWindow = false,
    int? ParentProcessId = null);

public interface IWeChatIdentityProvider
{
    WeChatRuntimeIdentity ResolveActiveProcess();

    WeChatRuntimeIdentity BindDataRoot(
        WeChatRuntimeIdentity runtime,
        string dataRoot);

    WeChatRuntimeIdentity ResolveActive(string dataRoot);
}

public sealed class WeChatIdentityProvider : IWeChatIdentityProvider
{
    public RecoveryEpochIdentity CreateIdentity(
        string executablePath,
        string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedExecutable = Path.GetFullPath(executablePath);
        if (!File.Exists(normalizedExecutable))
            throw new FileNotFoundException("Target executable does not exist.", normalizedExecutable);
        var normalizedRoot = NormalizeRoot(dataRoot);
        return new RecoveryEpochIdentity(
            ExecutableIdentity(normalizedExecutable),
            TextSha256(normalizedRoot));
    }

    public WeChatRuntimeIdentity ResolveActiveProcess()
    {
        var process = ResolveInteractiveProcess();
        return new WeChatRuntimeIdentity(
            process.Pid,
            process.SessionId,
            process.ExecutablePath,
            ExecutableIdentity(process.ExecutablePath));
    }

    public WeChatRuntimeIdentity BindDataRoot(
        WeChatRuntimeIdentity runtime,
        string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedRoot = NormalizeRoot(dataRoot);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException("The selected target data root is unavailable.");
        var identity = new RecoveryEpochIdentity(
            runtime.ExecutableIdentity,
            TextSha256(normalizedRoot));
        return new WeChatRuntimeIdentity(
            runtime.ProcessId,
            runtime.SessionId,
            runtime.ExecutablePath,
            runtime.ExecutableIdentity,
            identity,
            normalizedRoot);
    }

    public WeChatRuntimeIdentity ResolveActive(string dataRoot) =>
        BindDataRoot(ResolveActiveProcess(), dataRoot);

    private static (int Pid, int SessionId, string ExecutablePath) ResolveInteractiveProcess()
    {
        using var current = Process.GetCurrentProcess();
        var currentSession = current.SessionId;
        var parentProcessIds = SnapshotParentProcessIds();
        var candidates = new List<WeChatProcessCandidate>();
        foreach (var process in Process.GetProcessesByName("Weixin"))
        {
            try
            {
                if (process.SessionId != currentSession) continue;
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    candidates.Add(new WeChatProcessCandidate(
                        process.Id,
                        process.SessionId,
                        Path.GetFullPath(path),
                        process.MainWindowHandle != nint.Zero,
                        parentProcessIds.GetValueOrDefault(process.Id)));
                }
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process may exit or deny module inspection while it is enumerated.
            }
            finally
            {
                process.Dispose();
            }
        }

        var selected = SelectInteractiveProcess(candidates);
        return (selected.ProcessId, selected.SessionId, selected.ExecutablePath);
    }

    internal static WeChatProcessCandidate SelectInteractiveProcess(
        IReadOnlyList<WeChatProcessCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "No target process is active in the worker session.");
        if (candidates.Count == 1) return candidates[0];

        var candidateProcessIds = candidates
            .Select(candidate => candidate.ProcessId)
            .ToHashSet();
        var roots = candidates
            .Where(candidate =>
                candidate.ParentProcessId is int parentProcessId &&
                !candidateProcessIds.Contains(parentProcessId))
            .ToArray();
        if (roots.Length == 1) return roots[0];

        var windowed = candidates.Where(candidate => candidate.HasMainWindow).ToArray();
        if (windowed.Length == 1) return windowed[0];
        throw new AmbiguousWeChatProcessException(
            windowed.Length > 1 ? windowed.Length : candidates.Count);
    }

    private static IReadOnlyDictionary<int, int> SnapshotParentProcessIds()
    {
        if (!OperatingSystem.IsWindows()) return new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new nint(-1)) return new Dictionary<int, int>();

        try
        {
            var result = new Dictionary<int, int>();
            var entry = new ProcessEntry32
            {
                Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[checked((int)entry.ProcessId)] =
                    checked((int)entry.ParentProcessId);
                entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
            }
            while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
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

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        nint snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        nint snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static string NormalizeRoot(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static string ExecutableIdentity(string executablePath)
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "unknown";
        var executableHash = FileSha256(executablePath);
        var signer = SignerIdentity(executablePath);
        return $"{version}|sha256:{executableHash}|signer:{signer}";
    }

    private static string FileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string TextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string SignerIdentity(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return "unavailable";
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            return certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        }
        catch (CryptographicException)
        {
            return "unsigned";
        }
    }
}

