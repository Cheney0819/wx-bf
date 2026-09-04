using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Footprint.Receiver.Mac;

public sealed partial class KeychainNative : IKeychainGenericPasswordBackend
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    public static string LoginKeychainPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");

    public ValueTask<byte[]?> ReadAsync(string service, string account, CancellationToken cancellationToken)
    {
        EnsureMac();
        cancellationToken.ThrowIfCancellationRequested();
        var serviceBytes = System.Text.Encoding.UTF8.GetBytes(service);
        var accountBytes = System.Text.Encoding.UTF8.GetBytes(account);
        var keychain = OpenLoginKeychain();
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(keychain, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, out var length, out data, out item);
            if (status == ErrSecItemNotFound) return ValueTask.FromResult<byte[]?>(null);
            ThrowIfError(status);
            var result = new byte[length];
            Marshal.Copy(data, result, 0, checked((int)length));
            return ValueTask.FromResult<byte[]?>(result);
        }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
            CFRelease(keychain);
        }
    }

    public ValueTask WriteAsync(string service, string account, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        EnsureMac();
        cancellationToken.ThrowIfCancellationRequested();
        var serviceBytes = System.Text.Encoding.UTF8.GetBytes(service);
        var accountBytes = System.Text.Encoding.UTF8.GetBytes(account);
        var value = secret.ToArray();
        var keychain = OpenLoginKeychain();
        try
        {
            var find = SecKeychainFindGenericPassword(keychain, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, out _, out var oldData, out var item);
            if (oldData != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, oldData);
            if (find == ErrSecSuccess)
            {
                try { ThrowIfError(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)value.Length, value)); }
                finally { if (item != IntPtr.Zero) CFRelease(item); }
            }
            else if (find == ErrSecItemNotFound)
            {
                ThrowIfError(SecKeychainAddGenericPassword(keychain, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, (uint)value.Length, value, out var added));
                if (added != IntPtr.Zero) CFRelease(added);
            }
            else ThrowIfError(find);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
            CFRelease(keychain);
        }
        return ValueTask.CompletedTask;
    }

    private static void EnsureMac() { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Login Keychain 仅在 macOS 上可用。"); }
    private static IntPtr OpenLoginKeychain()
    {
        ThrowIfError(SecKeychainOpen(LoginKeychainPath, out var keychain));
        if (keychain == IntPtr.Zero) throw new InvalidOperationException("无法打开 Login Keychain。");
        return keychain;
    }
    private static void ThrowIfError(int status) { if (status != ErrSecSuccess) throw new Win32Exception(status, $"Keychain 操作失败（OSStatus {status}）。"); }

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathName, out IntPtr keychain);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] serviceName, uint accountLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] serviceName, uint accountLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attributes, uint length, byte[] data);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(IntPtr value);
}
