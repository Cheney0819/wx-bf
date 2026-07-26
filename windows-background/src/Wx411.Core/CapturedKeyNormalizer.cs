using System.Security.Cryptography;
using System.Text;

namespace Wx411.Core;

public sealed class NormalizedCapturedKey : IDisposable
{
    private byte[]? _key;

    internal NormalizedCapturedKey(string representation, byte[] key)
    {
        Representation = representation;
        _key = key;
    }

    public string Representation { get; }

    public byte[] Key => _key ?? [];

    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }
}

public static class CapturedKeyNormalizer
{
    private const int MaxPassphraseBytes = 128;

    public static IReadOnlyList<NormalizedCapturedKey> Normalize(byte[] captured, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != 16)
            throw new ArgumentException("Database salt must be exactly 16 bytes.", nameof(salt));

        var candidates = new List<NormalizedCapturedKey>();
        if (captured.Length == 32)
            candidates.Add(new NormalizedCapturedKey("Raw32", captured.AsSpan(0, 32).ToArray()));

        var keyText = LooksLikeKeyText(captured);
        if (keyText)
        {
            try
            {
                candidates.Add(new NormalizedCapturedKey("HexText", SqlCipher4.ParseKeyBytes(captured, salt)));
            }
            catch (FormatException)
            {
                DisposeAll(candidates);
                return [];
            }
        }

        if (!keyText && IsLikelyUtf8Passphrase(captured))
            candidates.Add(new NormalizedCapturedKey("Passphrase", SqlCipher4.DeriveRawKey(captured, salt)));

        return candidates;
    }

    private static bool LooksLikeKeyText(byte[] captured)
    {
        var value = TrimAsciiWhitespace(captured.AsSpan());
        if (value.Length >= 3 &&
            (value[0] == (byte)'x' || value[0] == (byte)'X') &&
            value[1] == (byte)'\'' &&
            value[^1] == (byte)'\'')
        {
            value = value[2..^1];
        }

        return value.Length is 64 or 96 && IsAsciiHex(value);
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

    private static bool IsAsciiHex(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
            if (!((item >= (byte)'0' && item <= (byte)'9') ||
                  (item >= (byte)'a' && item <= (byte)'f') ||
                  (item >= (byte)'A' && item <= (byte)'F')))
                return false;
        return true;
    }

    private static bool IsLikelyUtf8Passphrase(byte[] captured)
    {
        if (captured.Length is 0 or > MaxPassphraseBytes)
            return false;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(captured);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        foreach (var item in captured)
        {
            if (item < 0x20 && item is not ((byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        }

        return true;
    }

    private static void DisposeAll(IEnumerable<NormalizedCapturedKey> candidates)
    {
        foreach (var candidate in candidates)
            candidate.Dispose();
    }
}
