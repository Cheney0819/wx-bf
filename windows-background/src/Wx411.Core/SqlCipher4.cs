using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Wx411.Core;

public enum PageNumberEncoding
{
    LittleEndian,
    BigEndian,
}

public sealed record CipherProfile(
    string Name,
    int PageSize,
    int Reserve,
    int HmacSize,
    int HmacKdfIterations,
    int PassphraseKdfIterations,
    byte SaltXor,
    HashAlgorithmName HmacAlgorithm,
    HashAlgorithmName HmacKdfAlgorithm,
    PageNumberEncoding PageNumberEncoding);

public class IntegrityException : Exception
{
    public IntegrityException(string message) : base(message) { }
}

public sealed record PageAuthenticationReport(
    int PageCount,
    int FailedPageCount,
    IReadOnlyList<int> FailedPages)
{
    public bool IsValid => FailedPageCount == 0;
}

public sealed class PageAuthenticationException : IntegrityException
{
    public PageAuthenticationException(PageAuthenticationReport report)
        : base(FormatMessage(report))
    {
        Report = report;
    }

    public PageAuthenticationReport Report { get; }

    private static string FormatMessage(PageAuthenticationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var pages = string.Join(", ", report.FailedPages);
        return $"full-page HMAC authentication failed on {report.FailedPageCount} of " +
               $"{report.PageCount} pages: [{pages}]";
    }
}

public static class SqlCipher4
{
    public static readonly CipherProfile Profile = new(
        "sqlcipher4",
        PageSize: 4096,
        Reserve: 80,
        HmacSize: 64,
        HmacKdfIterations: 2,
        PassphraseKdfIterations: 256000,
        SaltXor: 0x3A,
        HmacAlgorithm: HashAlgorithmName.SHA512,
        HmacKdfAlgorithm: HashAlgorithmName.SHA512,
        PageNumberEncoding: PageNumberEncoding.LittleEndian);

    private static readonly byte[] Header = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static byte[] ParseKeyText(string text, byte[]? expectedSalt = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var encoded = Encoding.ASCII.GetBytes(text);
        try
        {
            return ParseKeyBytes(encoded, expectedSalt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public static byte[] ParseKeyBytes(byte[] encoded, byte[]? expectedSalt = null)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return ParseKeyBytes(encoded.AsSpan(), expectedSalt);
    }

    private static byte[] ParseKeyBytes(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> expectedSalt)
    {
        var value = TrimAsciiWhitespace(encoded);
        var payload = value;
        if (value.Length >= 3 && (value[0] == (byte)'x' || value[0] == (byte)'X') &&
            value[1] == (byte)'\'' && value[^1] == (byte)'\'')
        {
            payload = value[2..^1];
        }

        if (payload.Length is not (64 or 96))
            throw new FormatException("key text must contain 64 hex digits or 64+32 hex digits");

        var decoded = new byte[payload.Length / 2];
        try
        {
            for (var index = 0; index < decoded.Length; index++)
            {
                var high = HexNibble(payload[index * 2]);
                var low = HexNibble(payload[index * 2 + 1]);
                if (high < 0 || low < 0)
                    throw new FormatException("key text contains a non-hex character");
                decoded[index] = (byte)((high << 4) | low);
            }

            if (decoded.Length == 48 && !expectedSalt.IsEmpty &&
                !CryptographicOperations.FixedTimeEquals(decoded.AsSpan(32, 16), expectedSalt))
                throw new FormatException("embedded key salt does not match database salt");

            return decoded.AsSpan(0, 32).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start])) start++;
        var end = value.Length;
        while (end > start && IsAsciiWhitespace(value[end - 1])) end--;
        return value[start..end];
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static int HexNibble(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1,
    };

    public static byte[] DeriveRawKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt,
        int iterations = 256000)
    {
        ValidateSalt(salt);
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        var result = new byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                passphrase, salt, result, iterations, HashAlgorithmName.SHA512);
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    public static byte[] MakeMacKey(ReadOnlySpan<byte> rawKey, ReadOnlySpan<byte> salt,
        CipherProfile? profile = null)
    {
        profile ??= Profile;
        ValidateProfile(profile);
        ValidateKey(rawKey);
        ValidateSalt(salt);
        var macSalt = new byte[16];
        var result = new byte[32];
        for (var i = 0; i < macSalt.Length; i++) macSalt[i] = (byte)(salt[i] ^ profile.SaltXor);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                rawKey, macSalt, result, profile.HmacKdfIterations, profile.HmacKdfAlgorithm);
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macSalt);
        }
    }

    public static bool VerifyPage(ReadOnlySpan<byte> database, ReadOnlySpan<byte> rawKey,
        ReadOnlySpan<byte> salt, int pageNumber = 1, CipherProfile? profile = null)
    {
        profile ??= Profile;
        ValidateDatabase(database, profile);
        ValidateKey(rawKey);
        ValidateSalt(salt);
        if (pageNumber < 1 || pageNumber > database.Length / profile.PageSize) return false;

        var macKey = MakeMacKey(rawKey, salt, profile);
        try
        {
            return VerifyPageWithMacKey(database, macKey, pageNumber, profile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    public static PageAuthenticationReport AuthenticateDatabase(
        ReadOnlySpan<byte> database,
        ReadOnlySpan<byte> rawKey,
        CipherProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        profile ??= Profile;
        ValidateDatabase(database, profile);
        ValidateKey(rawKey);
        byte[]? salt = null;
        byte[]? macKey = null;
        try
        {
            salt = database[..16].ToArray();
            macKey = MakeMacKey(rawKey, salt, profile);
            return AuthenticateDatabaseWithMacKey(database, macKey, profile, cancellationToken);
        }
        finally
        {
            if (macKey is not null) CryptographicOperations.ZeroMemory(macKey);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static byte[] DecryptDatabase(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> rawKey,
        CipherProfile? profile = null, CancellationToken cancellationToken = default)
    {
        profile ??= Profile;
        ValidateDatabase(encrypted, profile);
        ValidateKey(rawKey);
        byte[]? salt = null;
        byte[]? macKey = null;
        byte[]? output = null;
        var completed = false;
        try
        {
            salt = encrypted[..16].ToArray();
            macKey = MakeMacKey(rawKey, salt, profile);
            var authentication = AuthenticateDatabaseWithMacKey(
                encrypted,
                macKey,
                profile,
                cancellationToken);
            if (!authentication.IsValid)
                throw new PageAuthenticationException(authentication);

            output = new byte[encrypted.Length];
            var pageCount = encrypted.Length / profile.PageSize;
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageStart = (pageNumber - 1) * profile.PageSize;
                if (pageNumber > 1 && IsZeroPage(encrypted.Slice(pageStart, profile.PageSize)))
                    continue;

                var bodyStart = pageStart + (pageNumber == 1 ? 16 : 0);
                var ivStart = pageStart + profile.PageSize - profile.Reserve;
                var bodyLength = ivStart - bodyStart;

                byte[]? iv = null;
                byte[]? cipherBody = null;
                byte[]? plainBody = null;
                byte[]? aesKey = null;
                try
                {
                    iv = encrypted.Slice(ivStart, 16).ToArray();
                    cipherBody = encrypted.Slice(bodyStart, bodyLength).ToArray();
                    aesKey = rawKey.ToArray();
                    using var aes = Aes.Create();
                    aes.Key = aesKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    using var decryptor = aes.CreateDecryptor();
                    plainBody = decryptor.TransformFinalBlock(cipherBody, 0, cipherBody.Length);

                    if (pageNumber == 1) Header.CopyTo(output, 0);
                    plainBody.CopyTo(output, pageNumber == 1 ? 16 : pageStart);
                }
                finally
                {
                    if (cipherBody is not null) CryptographicOperations.ZeroMemory(cipherBody);
                    if (plainBody is not null) CryptographicOperations.ZeroMemory(plainBody);
                    if (iv is not null) CryptographicOperations.ZeroMemory(iv);
                    if (aesKey is not null) CryptographicOperations.ZeroMemory(aesKey);
                }
            }
            completed = true;
            return output;
        }
        finally
        {
            if (!completed && output is not null) CryptographicOperations.ZeroMemory(output);
            if (macKey is not null) CryptographicOperations.ZeroMemory(macKey);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static PageAuthenticationReport AuthenticateDatabaseWithMacKey(
        ReadOnlySpan<byte> database,
        byte[] macKey,
        CipherProfile profile,
        CancellationToken cancellationToken)
    {
        var pageCount = database.Length / profile.PageSize;
        var failedPages = new List<int>();
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageStart = (pageNumber - 1) * profile.PageSize;
            if (pageNumber > 1 && IsZeroPage(database.Slice(pageStart, profile.PageSize)))
                continue;

            if (!VerifyPageWithMacKey(database, macKey, pageNumber, profile))
                failedPages.Add(pageNumber);
        }

        return new PageAuthenticationReport(
            pageCount,
            failedPages.Count,
            Array.AsReadOnly(failedPages.ToArray()));
    }

    private static bool IsZeroPage(ReadOnlySpan<byte> page)
    {
        foreach (var value in page)
        {
            if (value != 0) return false;
        }

        return true;
    }

    internal static bool VerifyPageWithMacKey(ReadOnlySpan<byte> database, byte[] macKey,
        int pageNumber, CipherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(macKey);
        ValidateProfile(profile);
        if (macKey.Length != 32) throw new ArgumentException("MAC key must be 32 bytes.", nameof(macKey));
        var pageStart = (pageNumber - 1) * profile.PageSize;
        return VerifyEncryptedPageWithMacKey(
            database.Slice(pageStart, profile.PageSize),
            macKey,
            pageNumber,
            profile);
    }

    internal static bool VerifyEncryptedPageWithMacKey(
        ReadOnlySpan<byte> encryptedPage,
        byte[] macKey,
        int pageNumber,
        CipherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(macKey);
        ValidateProfile(profile);
        if (encryptedPage.Length != profile.PageSize)
            throw new ArgumentException("Encrypted page has the wrong size.", nameof(encryptedPage));
        if (macKey.Length != 32) throw new ArgumentException("MAC key must be 32 bytes.", nameof(macKey));
        if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var bodyStart = pageNumber == 1 ? 16 : 0;
        var ivStart = profile.PageSize - profile.Reserve;
        var tagStart = ivStart + 16;
        var bodyAndIvLength = tagStart - bodyStart;
        var expected = encryptedPage.Slice(tagStart, profile.HmacSize);
        Span<byte> encodedPageNumber = stackalloc byte[4];
        if (profile.PageNumberEncoding == PageNumberEncoding.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(encodedPageNumber, (uint)pageNumber);
        else
            BinaryPrimitives.WriteUInt32BigEndian(encodedPageNumber, (uint)pageNumber);

        Span<byte> actual = stackalloc byte[64];
        using var hmac = IncrementalHash.CreateHMAC(profile.HmacAlgorithm, macKey);
        hmac.AppendData(encryptedPage.Slice(bodyStart, bodyAndIvLength));
        hmac.AppendData(encodedPageNumber);
        if (!hmac.TryGetHashAndReset(actual, out var written) || written < profile.HmacSize)
            throw new CryptographicException("Could not calculate the page HMAC.");
        var valid = CryptographicOperations.FixedTimeEquals(actual[..profile.HmacSize], expected);
        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(encodedPageNumber);
        return valid;
    }

    private static void ValidateDatabase(ReadOnlySpan<byte> database, CipherProfile profile)
    {
        ValidateProfile(profile);
        if (database.Length < profile.PageSize || database.Length % profile.PageSize != 0)
        {
            throw new IntegrityException("database size is not a whole number of pages");
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("raw key must be 32 bytes", nameof(key));
    }

    private static void ValidateSalt(ReadOnlySpan<byte> salt)
    {
        if (salt.Length != 16) throw new ArgumentException("database salt must be 16 bytes", nameof(salt));
    }

    internal static void ValidateProfile(CipherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name must not be empty.", nameof(profile));
        if (profile.PageSize is < 512 or > 65536 ||
            (profile.PageSize & (profile.PageSize - 1)) != 0)
            throw new ArgumentException("Page size must be a power of two from 512 through 65536.", nameof(profile));
        var digestSize = GetDigestSize(profile.HmacAlgorithm);
        _ = GetDigestSize(profile.HmacKdfAlgorithm);
        if (profile.HmacSize != digestSize)
            throw new ArgumentException("HMAC size must equal the selected digest size.", nameof(profile));
        if (profile.HmacKdfIterations <= 0 || profile.PassphraseKdfIterations <= 0)
            throw new ArgumentException("KDF iteration counts must be positive.", nameof(profile));
        if (profile.Reserve < 16 + profile.HmacSize ||
            profile.Reserve >= profile.PageSize ||
            profile.Reserve % 16 != 0)
            throw new ArgumentException("Reserve bytes do not fit IV, HMAC, and AES alignment.", nameof(profile));
        if ((profile.PageSize - profile.Reserve) % 16 != 0 ||
            (profile.PageSize - profile.Reserve - 16) % 16 != 0)
            throw new ArgumentException("Encrypted page bodies must be AES-block aligned.", nameof(profile));
        if (!Enum.IsDefined(profile.PageNumberEncoding))
            throw new ArgumentException("Page number encoding is invalid.", nameof(profile));
    }

    private static int GetDigestSize(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA1) return 20;
        if (algorithm == HashAlgorithmName.SHA256) return 32;
        if (algorithm == HashAlgorithmName.SHA512) return 64;
        throw new ArgumentException($"Unsupported hash algorithm: {algorithm.Name ?? "<empty>"}.");
    }
}
