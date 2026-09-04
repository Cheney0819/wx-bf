using System.Security.Cryptography;
using System.Text;

namespace Footprint.Receiver.Mac;

public static class ReceiverToken
{
    public const int EncodedLength = 43;
    public const int DecodedLength = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(string? token)
    {
        if (token is null) throw new InvalidDataException("Receiver Token 格式无效。");
        Validate(token.AsSpan());
    }

    public static void Validate(ReadOnlySpan<char> token)
    {
        if (token.Length != EncodedLength) throw new InvalidDataException("Receiver Token 格式无效。");
        Span<char> base64 = stackalloc char[44];
        Span<char> canonical = stackalloc char[44];
        Span<byte> decoded = stackalloc byte[DecodedLength];
        try
        {
            for (var index = 0; index < token.Length; index++)
            {
                var value = token[index];
                if (!(char.IsAsciiLetterOrDigit(value) || value is '-' or '_')) throw new InvalidDataException("Receiver Token 格式无效。");
                base64[index] = value switch { '-' => '+', '_' => '/', _ => value };
            }
            base64[43] = '=';
            if (!Convert.TryFromBase64Chars(base64, decoded, out var written) || written != DecodedLength) throw new InvalidDataException("Receiver Token 长度无效。");
            if (!Convert.TryToBase64Chars(decoded, canonical, out var canonicalWritten) || canonicalWritten != canonical.Length || canonical[43] != '=')
                throw new InvalidDataException("Receiver Token 格式无效。");
            var mismatch = 0;
            for (var index = 0; index < token.Length; index++)
            {
                var expected = canonical[index] switch { '+' => '-', '/' => '_', _ => canonical[index] };
                mismatch |= token[index] ^ expected;
            }
            if (mismatch != 0) throw new InvalidDataException("Receiver Token 格式无效。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            base64.Clear();
            canonical.Clear();
        }
    }

    public static string DecodeAndValidate(byte[] bytes)
    {
        string token;
        try { token = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Receiver Token 不是有效 UTF-8。", exception); }
        Validate(token);
        return token;
    }
}
