using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopPet.Background.Contracts;

namespace DesktopPet.Recovery;

public sealed record WeChatRuntimeIdentity(
    RecoveryEpochIdentity EpochIdentity,
    string ExecutablePath,
    string DataRoot);

public sealed class WeChatIdentityProvider
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
        var versionInfo = FileVersionInfo.GetVersionInfo(normalizedExecutable);
        var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "unknown";
        var executableHash = FileSha256(normalizedExecutable);
        var signer = SignerIdentity(normalizedExecutable);
        var executableIdentity = $"{version}|sha256:{executableHash}|signer:{signer}";
        return new RecoveryEpochIdentity(
            executableIdentity,
            TextSha256(normalizedRoot));
    }

    public WeChatRuntimeIdentity ResolveActive(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var executablePath = ResolveInteractiveExecutable();
        var normalizedRoot = NormalizeRoot(dataRoot);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException("The selected target data root is unavailable.");
        return new WeChatRuntimeIdentity(
            CreateIdentity(executablePath, normalizedRoot),
            executablePath,
            normalizedRoot);
    }

    private static string ResolveInteractiveExecutable()
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
                    return Path.GetFullPath(path);
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
