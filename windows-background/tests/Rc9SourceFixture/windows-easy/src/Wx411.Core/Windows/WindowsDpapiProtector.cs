using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Wx411.Core.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiProtector : ICapturePayloadProtector
{
    private const uint CryptProtectUiForbidden = 0x1;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        Transform(plaintext, entropy, protect: true);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
        Transform(ciphertext, entropy, protect: false);

    private static byte[] Transform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> entropy,
        bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        if (input.IsEmpty) throw new ArgumentException("DPAPI input must not be empty.", nameof(input));

        var inputBytes = input.ToArray();
        var entropyBytes = entropy.ToArray();
        var inputBlob = AllocateBlob(inputBytes);
        var entropyBlob = AllocateBlob(entropyBytes);
        DataBlob outputBlob = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new CryptographicException(
                    "Windows DPAPI operation failed.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroAndFree(inputBlob, localFree: false);
            ZeroAndFree(entropyBlob, localFree: false);
            ZeroAndFree(outputBlob, localFree: true);
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
        }
    }

    private static DataBlob AllocateBlob(byte[] bytes)
    {
        if (bytes.Length == 0) return default;
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob(bytes.Length, pointer);
    }

    private static void ZeroAndFree(DataBlob blob, bool localFree)
    {
        if (blob.Data == IntPtr.Zero) return;
        if (blob.Length > 0)
        {
            var zeros = new byte[blob.Length];
            Marshal.Copy(zeros, 0, blob.Data, zeros.Length);
        }
        if (localFree)
            _ = LocalFree(blob.Data);
        else
            Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DataBlob(int Length, IntPtr Data);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
