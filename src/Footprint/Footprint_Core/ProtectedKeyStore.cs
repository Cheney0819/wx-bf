using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Footprint.Core;

public static class ProtectedKeyStore
{
    public static void ProtectToFile(ReadOnlySpan<byte> value, string path)
        => ProtectToFile(value, path, default);

    public static void ProtectToFile(ReadOnlySpan<byte> value, string path, ReadOnlySpan<byte> optionalEntropy)
    {
        var encrypted = Protect(value, optionalEntropy);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static byte[] UnprotectFromFile(string path)
        => UnprotectFromFile(path, default);

    public static byte[] UnprotectFromFile(string path, ReadOnlySpan<byte> optionalEntropy)
    {
        var encrypted = File.ReadAllBytes(path);
        try { return Unprotect(encrypted, optionalEntropy); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    public static byte[] Protect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> optionalEntropy = default) =>
        CryptWithCopies(false, value, optionalEntropy);

    public static byte[] Unprotect(ReadOnlySpan<byte> value, ReadOnlySpan<byte> optionalEntropy = default) =>
        CryptWithCopies(true, value, optionalEntropy);

    private static byte[] CryptWithCopies(bool decrypt, ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> optionalEntropy)
    {
        EnsureWindows();
        var input = value.ToArray();
        var entropy = optionalEntropy.ToArray();
        try { return Crypt(decrypt, input, entropy); }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static byte[] Crypt(bool decrypt, byte[] value, byte[] entropyValue)
    {
        var input = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        var entropyPointer = IntPtr.Zero;
        try
        {
            input = Allocate(value);
            if (entropyValue.Length > 0)
            {
                entropy = Allocate(entropyValue);
                entropyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
                Marshal.StructureToPtr(entropy, entropyPointer, false);
            }
            var ok = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, entropyPointer, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptProtectData(ref input, "Footprint key", entropyPointer, IntPtr.Zero, IntPtr.Zero, 0, ref output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), decrypt
                    ? "Windows DPAPI CurrentUser 解密失败。"
                    : "Windows DPAPI CurrentUser 加密失败。");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroAndFree(input);
            ZeroAndFree(entropy);
            if (entropyPointer != IntPtr.Zero)
            {
                for (var i = 0; i < Marshal.SizeOf<DataBlob>(); i++) Marshal.WriteByte(entropyPointer, i, 0);
                Marshal.FreeHGlobal(entropyPointer);
            }
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob Allocate(byte[] value)
    {
        var result = new DataBlob { Size = value.Length };
        if (value.Length == 0) return result;
        result.Data = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, result.Data, value.Length);
        return result;
    }

    private static void ZeroAndFree(DataBlob value)
    {
        if (value.Data == IntPtr.Zero) return;
        for (var i = 0; i < value.Size; i++) Marshal.WriteByte(value.Data, i, 0);
        Marshal.FreeHGlobal(value.Data);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("仅支持 Windows DPAPI CurrentUser。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
