using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Wx411.Core.Tests;

public sealed class CapturedKeyNormalizerTests
{
    [Fact]
    public void DerivesOrdinaryUtf8Passphrase()
    {
        var passphrase = Encoding.UTF8.GetBytes("ordinary-passphrase-密钥");
        var salt = Enumerable.Range(0x40, 16).Select(value => (byte)value).ToArray();
        var expected = SqlCipher4.DeriveRawKey(passphrase, salt);
        var candidates = Normalize(passphrase, salt);

        try
        {
            var candidate = Assert.Single(candidates);
            Assert.Equal("Passphrase", Representation(candidate));
            Assert.Equal(expected, Key(candidate));
            Assert.IsAssignableFrom<IDisposable>(candidate);
        }
        finally
        {
            DisposeAll(candidates);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(passphrase);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    [Fact]
    public void ThirtyTwoByteTextTriesRawThenPassphrase()
    {
        var captured = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
        var salt = new byte[16];
        var candidates = Normalize(captured, salt);

        try
        {
            Assert.Equal(["Raw32", "Passphrase"], candidates.Select(Representation).ToArray());
            Assert.Equal(captured, Key(candidates[0]));
            Assert.Equal(SqlCipher4.DeriveRawKey(captured, salt), Key(candidates[1]));
        }
        finally
        {
            DisposeAll(candidates);
            CryptographicOperations.ZeroMemory(captured);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    [Fact]
    public void WrongEmbeddedSaltIsNotReinterpretedAsPassphrase()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var expectedSalt = Enumerable.Range(0x20, 16).Select(value => (byte)value).ToArray();
        var wrongSalt = Enumerable.Range(0x60, 16).Select(value => (byte)value).ToArray();
        var captured = Encoding.ASCII.GetBytes(
            $"x'{Convert.ToHexString(key)}{Convert.ToHexString(wrongSalt)}'");
        var candidates = Normalize(captured, expectedSalt);

        try
        {
            Assert.Empty(candidates);
        }
        finally
        {
            DisposeAll(candidates);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(expectedSalt);
            CryptographicOperations.ZeroMemory(wrongSalt);
            CryptographicOperations.ZeroMemory(captured);
        }
    }

    private static object[] Normalize(byte[] captured, byte[] salt)
    {
        var type = typeof(SqlCipher4).Assembly.GetType("Wx411.Core.CapturedKeyNormalizer");
        Assert.NotNull(type);
        var method = type!.GetMethod(
            "Normalize",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(byte[]), typeof(byte[])],
            modifiers: null);
        Assert.NotNull(method);
        var result = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(null, [captured, salt]));
        return result.Cast<object>().ToArray();
    }

    private static string Representation(object candidate) =>
        candidate.GetType().GetProperty("Representation")!.GetValue(candidate)!.ToString()!;

    private static byte[] Key(object candidate) =>
        Assert.IsType<byte[]>(candidate.GetType().GetProperty("Key")!.GetValue(candidate));

    private static void DisposeAll(IEnumerable<object> candidates)
    {
        foreach (var candidate in candidates)
            (candidate as IDisposable)?.Dispose();
    }
}
