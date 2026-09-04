using Footprint.Receiver.Network;

namespace Footprint.Receiver.Internal;

internal sealed class RunLease : IDisposable
{
    private readonly FileStream _stream;

    private RunLease(FileStream stream) => _stream = stream;

    public static RunLease? TryAcquire(string packagesRoot, string runId)
    {
        PackageIdentity.ValidateRunId(runId);
        packagesRoot = Path.GetFullPath(packagesRoot);
        UnixDurability.SecureDirectory(packagesRoot);
        RejectLink(packagesRoot);
        var locksRoot = Path.Combine(packagesRoot, ".locks");
        UnixDurability.SecureDirectory(locksRoot);
        RejectLink(locksRoot);
        var lockPath = Path.Combine(locksRoot, runId + ".lock");
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return null;
        }

        try
        {
            UnixDurability.SecureFile(lockPath);
            return new RunLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    private static bool IsLockContention(IOException exception)
    {
        var nativeError = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33
            : nativeError is 11 or 35;
    }

    private static void RejectLink(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Run lease 目录不能是链接。");
    }
}
