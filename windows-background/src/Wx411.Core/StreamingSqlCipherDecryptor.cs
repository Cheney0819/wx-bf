using System.Security.Cryptography;
using System.Text;

namespace Wx411.Core;

internal static class StreamingSqlCipherDecryptor
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("SQLite format 3\0");

    internal static bool IsZeroPage(ReadOnlySpan<byte> page)
    {
        foreach (var value in page)
        {
            if (value != 0) return false;
        }
        return true;
    }

    internal static void DecryptPage(
        byte[] encryptedPage,
        byte[] plaintextPage,
        byte[] rawKey,
        int pageNumber,
        CipherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(encryptedPage);
        ArgumentNullException.ThrowIfNull(plaintextPage);
        ArgumentNullException.ThrowIfNull(rawKey);
        if (encryptedPage.Length != profile.PageSize || plaintextPage.Length != profile.PageSize)
            throw new ArgumentException("Page buffers must match the cipher profile page size.");
        Array.Clear(plaintextPage);
        if (pageNumber > 1 && IsZeroPage(encryptedPage)) return;

        var bodyStart = pageNumber == 1 ? 16 : 0;
        var ivStart = profile.PageSize - profile.Reserve;
        var bodyLength = ivStart - bodyStart;
        var iv = encryptedPage.AsSpan(ivStart, 16).ToArray();
        try
        {
            using var aes = Aes.Create();
            aes.Key = rawKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            var written = decryptor.TransformBlock(
                encryptedPage,
                bodyStart,
                bodyLength,
                plaintextPage,
                bodyStart);
            if (written != bodyLength)
                throw new CryptographicException("SQLCipher page decryption returned a short block.");
            if (pageNumber == 1) Header.CopyTo(plaintextPage, 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
        }
    }
}
