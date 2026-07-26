using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wx411.Core;

namespace Wx411.Core.Tests;

internal sealed record CipherFixture(
    byte[] Encrypted,
    byte[] Plaintext,
    byte[] Key,
    byte[] Salt);

internal static class CipherFixtureFactory
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static CipherFixture Create(
        CipherProfile profile,
        int pageCount = 4,
        int keyOffset = 0,
        int saltOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (pageCount <= 0) throw new ArgumentOutOfRangeException(nameof(pageCount));

        var key = Enumerable.Range(0, 32).Select(index => (byte)(index * 7 + 3 + keyOffset)).ToArray();
        var salt = Enumerable.Range(0, 16).Select(index => (byte)(0xA0 + index + saltOffset)).ToArray();
        var encrypted = new byte[checked(profile.PageSize * pageCount)];
        var plaintext = new byte[encrypted.Length];
        salt.CopyTo(encrypted, 0);
        Header.CopyTo(plaintext, 0);

        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var pageStart = checked((pageNumber - 1) * profile.PageSize);
            var bodyStart = pageStart + (pageNumber == 1 ? 16 : 0);
            var ivStart = pageStart + profile.PageSize - profile.Reserve;
            var bodyLength = ivStart - bodyStart;
            if (bodyLength <= 0 || bodyLength % 16 != 0)
                throw new ArgumentException("Fixture profile has an invalid AES body length.", nameof(profile));

            for (var index = 0; index < bodyLength; index++)
            {
                plaintext[bodyStart + index] = (byte)(pageNumber * 37 + index * 13 + 11);
            }

            var iv = Enumerable.Range(0, 16)
                .Select(index => (byte)(pageNumber * 19 + index * 5 + 1))
                .ToArray();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();
            var cipherBody = encryptor.TransformFinalBlock(plaintext, bodyStart, bodyLength);
            cipherBody.CopyTo(encrypted, bodyStart);
            iv.CopyTo(encrypted, ivStart);
            CryptographicOperations.ZeroMemory(cipherBody);
            CryptographicOperations.ZeroMemory(iv);
        }

        var macKey = DeriveMacKey(key, salt, profile);
        try
        {
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                WritePageTag(encrypted, macKey, pageNumber, profile);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }

        return new CipherFixture(encrypted, plaintext, key, salt);
    }

    public static byte[] CorruptPageTag(
        ReadOnlySpan<byte> encrypted,
        CipherProfile profile,
        int pageNumber)
    {
        var copy = encrypted.ToArray();
        var pageCount = copy.Length / profile.PageSize;
        if (pageNumber < 1 || pageNumber > pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var pageStart = (pageNumber - 1) * profile.PageSize;
        var tagStart = pageStart + profile.PageSize - profile.Reserve + 16;
        copy[tagStart] ^= 0x80;
        return copy;
    }

    private static byte[] DeriveMacKey(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> salt,
        CipherProfile profile)
    {
        var macSalt = salt.ToArray();
        for (var index = 0; index < macSalt.Length; index++) macSalt[index] ^= profile.SaltXor;
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                key.ToArray(),
                macSalt,
                profile.HmacKdfIterations,
                profile.HmacKdfAlgorithm,
                32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macSalt);
        }
    }

    private static void WritePageTag(
        byte[] encrypted,
        byte[] macKey,
        int pageNumber,
        CipherProfile profile)
    {
        var pageStart = (pageNumber - 1) * profile.PageSize;
        var bodyStart = pageStart + (pageNumber == 1 ? 16 : 0);
        var ivStart = pageStart + profile.PageSize - profile.Reserve;
        var tagStart = ivStart + 16;
        var message = new byte[tagStart - bodyStart + 4];
        encrypted.AsSpan(bodyStart, tagStart - bodyStart).CopyTo(message);
        if (profile.PageNumberEncoding == PageNumberEncoding.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(message.Length - 4), (uint)pageNumber);
        else
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(message.Length - 4), (uint)pageNumber);

        using var hmac = CreateHmac(profile.HmacAlgorithm, macKey);
        var tag = hmac.ComputeHash(message);
        tag.AsSpan(0, profile.HmacSize).CopyTo(encrypted.AsSpan(tagStart));
        CryptographicOperations.ZeroMemory(message);
        CryptographicOperations.ZeroMemory(tag);
    }

    private static HMAC CreateHmac(HashAlgorithmName algorithm, byte[] key)
    {
        if (algorithm == HashAlgorithmName.SHA1) return new HMACSHA1(key);
        if (algorithm == HashAlgorithmName.SHA256) return new HMACSHA256(key);
        if (algorithm == HashAlgorithmName.SHA512) return new HMACSHA512(key);
        throw new ArgumentException($"Unsupported fixture HMAC algorithm: {algorithm.Name}");
    }
}
