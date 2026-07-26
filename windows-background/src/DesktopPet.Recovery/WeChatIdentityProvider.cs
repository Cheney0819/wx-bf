using System.ComponentModel;
using System.Diagnostics;
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
        foreach (var process in Process.GetProcessesByName("Weixin")
                     .OrderBy(item => item.Id))
        {
            try
            {
                if (process.SessionId != currentSession) continue;
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return (process.Id, process.SessionId, Path.GetFullPath(path));
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
        throw new InvalidOperationException("No target process is active in the worker session.");
    }

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
