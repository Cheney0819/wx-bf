using System.Security.Cryptography;
using System.Text;
using Footprint.Receiver.Internal;
using Footprint.Receiver.Network;

namespace Footprint.Receiver.Configuration;

public interface IReceiverDeviceIdentity
{
    string GetStableDeviceId();
}

public sealed class ReceiverDeviceIdentity(string? path = null) : IReceiverDeviceIdentity
{
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Deskmate Footprint", "receiver-device-id"));

    public string GetStableDeviceId()
    {
        if (File.Exists(_path)) return ReadExisting();
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("设备标识路径缺少父目录。");
        UnixDurability.SecureDirectory(directory);
        var random = RandomNumberGenerator.GetBytes(16);
        var deviceId = "mac-" + Convert.ToHexString(random).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(random);
        var partial = _path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var bytes = Encoding.ASCII.GetBytes(deviceId);
            try
            {
                using var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                UnixDurability.SecureFile(partial);
                stream.Write(bytes);
                stream.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            try { File.Move(partial, _path, false); }
            catch (IOException) when (File.Exists(_path)) { }
            UnixDurability.SecureFile(_path);
            UnixDurability.FlushDirectory(directory);
            return ReadExisting();
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private string ReadExisting()
    {
        var info = new FileInfo(_path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("设备标识文件不能是链接。");
        UnixDurability.SecureFile(_path);
        var value = File.ReadAllText(_path, Encoding.ASCII);
        ReceiverEnrollmentClient.ValidateDeviceId(value);
        return value;
    }
}
