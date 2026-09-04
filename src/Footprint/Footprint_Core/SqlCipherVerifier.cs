using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Footprint.Core;

public static class SqlCipherOutputParser
{
    public static VerificationTrial Parse(int compatibility, int pageSize, int exitCode,
        ReadOnlyMemory<byte> output, ReadOnlyMemory<byte> error, bool timedOut = false,
        bool streamDrainTimedOut = false)
    {
        var outputSpan = output.Span;
        var cipherVersion = FindMarkerValue(outputSpan, "__WAC_CIPHER__="u8, version: true);
        var cipherIntegrity = FindMarkerValue(outputSpan, "__WAC_CIPHER_INTEGRITY__="u8, version: false);
        var integrity = FindMarkerValue(outputSpan, "__WAC_INTEGRITY__="u8, version: false);
        var countBytes = FindMarkerBytes(outputSpan, "__WAC_SCHEMA_COUNT__="u8);
        var count = ParsePositiveInteger(countBytes);
        if (string.IsNullOrWhiteSpace(cipherIntegrity) &&
            (ContainsLine(outputSpan, "__WAC_CIPHER_INTEGRITY_UNSUPPORTED__"u8) || exitCode == 0))
            cipherIntegrity = "unsupported-by-4.1.0";
        return new VerificationTrial(compatibility, exitCode, cipherVersion, cipherIntegrity, integrity, count,
            pageSize, Redactor.Redact(error, []), timedOut, streamDrainTimedOut);

        static string? FindMarkerValue(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> marker, bool version)
        {
            var value = FindMarkerBytes(bytes, marker);
            return (version ? IsCipherVersion(value) : IsIntegrity(value)) ? Encoding.ASCII.GetString(value) : null;
        }

        static ReadOnlySpan<byte> FindMarkerBytes(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> marker)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = bytes[offset..];
                var end = remaining.IndexOf((byte)'\n');
                var line = Trim(end < 0 ? remaining : remaining[..end]);
                offset += end < 0 ? remaining.Length : end + 1;
                if (!line.StartsWith(marker)) continue;
                var inline = Trim(line[marker.Length..]);
                if (!inline.IsEmpty) return inline;
                while (offset < bytes.Length)
                {
                    remaining = bytes[offset..];
                    end = remaining.IndexOf((byte)'\n');
                    line = Trim(end < 0 ? remaining : remaining[..end]);
                    offset += end < 0 ? remaining.Length : end + 1;
                    if (line.StartsWith("__WAC_"u8)) return [];
                    if (!line.IsEmpty) return line;
                }
                return [];
            }
            return [];
        }

        static bool ContainsLine(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = bytes[offset..];
                var end = remaining.IndexOf((byte)'\n');
                var line = Trim(end < 0 ? remaining : remaining[..end]);
                if (line.SequenceEqual(expected)) return true;
                offset += end < 0 ? remaining.Length : end + 1;
            }
            return false;
        }

        static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t' or (byte)'\r') value = value[1..];
            while (!value.IsEmpty && value[^1] is (byte)' ' or (byte)'\t' or (byte)'\r') value = value[..^1];
            return value;
        }

        static bool IsCipherVersion(ReadOnlySpan<byte> value)
        {
            if (value.Length is < 5 or > 96 || value[0] is < (byte)'0' or > (byte)'9') return false;
            var dots = 0;
            foreach (var item in value)
            {
                if (item == (byte)'.') dots++;
                else if (item is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'A' and <= (byte)'Z') and
                         not (>= (byte)'a' and <= (byte)'z') and not (byte)' ' and not (byte)'-' and not (byte)'_')
                    return false;
            }
            return dots >= 2;
        }

        static bool IsIntegrity(ReadOnlySpan<byte> value) => value.Length == 2 &&
            value[0] is (byte)'o' or (byte)'O' && value[1] is (byte)'k' or (byte)'K';

        static int ParsePositiveInteger(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty || value.Length > 10) return 0;
            var result = 0;
            foreach (var item in value)
            {
                if (item is < (byte)'0' or > (byte)'9') return 0;
                try { result = checked(result * 10 + item - (byte)'0'); }
                catch (OverflowException) { return 0; }
            }
            return result;
        }
    }

    // Legacy string overload. Byte-oriented callers retain the secure overload above.
    public static VerificationTrial Parse(int compatibility, int pageSize, int exitCode, string output, string error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var outputBytes = Encoding.UTF8.GetBytes(output);
        var errorBytes = Encoding.UTF8.GetBytes(error);
        try { return Parse(compatibility, pageSize, exitCode, outputBytes, errorBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(outputBytes);
            CryptographicOperations.ZeroMemory(errorBytes);
        }
    }
}

public static class Redactor
{
    public static string Redact(ReadOnlyMemory<byte> value, ReadOnlySpan<byte> key)
    {
        if (value.IsEmpty) return string.Empty;
        var redacted = ByteKeyRedactor.Redact(value.Span, key);
        try { return Encoding.UTF8.GetString(redacted); }
        finally { CryptographicOperations.ZeroMemory(redacted); }
    }

    public static string Redact(string value, ReadOnlySpan<byte> key)
    {
        if (string.IsNullOrEmpty(value) || key.IsEmpty) return value;
        var redacted = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (TryMatchRaw(value, index, key, out var rawLength) ||
                TryMatchHex(value, index, key, out rawLength))
            {
                redacted.Append("[REDACTED-KEY]");
                index += rawLength;
                continue;
            }
            redacted.Append(value[index++]);
        }
        return redacted.ToString();
    }

    private static bool TryMatchRaw(string value, int start, ReadOnlySpan<byte> key, out int consumed)
    {
        consumed = 0;
        if (key.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0 || value.Length - start < key.Length)
            return false;
        for (var i = 0; i < key.Length; i++)
            if (value[start + i] != (char)key[i]) return false;
        consumed = key.Length;
        return true;
    }

    private static bool TryMatchHex(string value, int start, ReadOnlySpan<byte> key, out int consumed)
    {
        consumed = 0;
        var current = start;
        char closingQuote = '\0';
        if (current + 2 <= value.Length && (value[current] is 'x' or 'X') &&
            value[current + 1] is '\'' or '"')
        {
            closingQuote = value[current + 1];
            current += 2;
        }

        for (var keyIndex = 0; keyIndex < key.Length; keyIndex++)
        {
            if (current + 2 <= value.Length && value[current] == '0' && value[current + 1] is 'x' or 'X')
                current += 2;
            if (current + 2 > value.Length ||
                HexNibble(value[current]) != key[keyIndex] >> 4 ||
                HexNibble(value[current + 1]) != (key[keyIndex] & 0x0f))
                return false;
            current += 2;
            if (keyIndex == key.Length - 1) continue;
            while (current < value.Length && IsHexSeparator(value[current])) current++;
        }

        if (closingQuote != '\0')
        {
            if (current >= value.Length || value[current] != closingQuote) return false;
            current++;
        }
        consumed = current - start;
        return true;
    }

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };

    private static bool IsHexSeparator(char value) => value is ':' or '-' or '_' or ',' or ' ' or '\t' or '\r' or '\n';
}

internal static class ByteKeyRedactor
{
    internal static readonly byte[] Replacement = "[REDACTED-KEY]"u8.ToArray();

    public static byte[] Redact(ReadOnlySpan<byte> value, ReadOnlySpan<byte> key)
    {
        if (value.IsEmpty) return [];
        if (key.IsEmpty) return value.ToArray();
        using var variants = new SensitiveByteVariants(key);
        var result = new byte[checked(value.Length + Replacement.Length * 4)];
        var written = 0;
        for (var index = 0; index < value.Length;)
        {
            if (variants.TryMatchAt(value, index, out var consumed))
            {
                EnsureCapacity(ref result, written + Replacement.Length);
                Replacement.CopyTo(result.AsSpan(written));
                written += Replacement.Length;
                index += consumed;
                continue;
            }
            EnsureCapacity(ref result, written + 1);
            result[written++] = value[index++];
        }
        var exact = result.AsSpan(0, written).ToArray();
        CryptographicOperations.ZeroMemory(result);
        return exact;
    }

    private static void EnsureCapacity(ref byte[] value, int required)
    {
        if (required <= value.Length) return;
        var replacement = new byte[Math.Max(required, checked(value.Length * 2))];
        value.CopyTo(replacement, 0);
        CryptographicOperations.ZeroMemory(value);
        value = replacement;
    }
}

internal sealed class SensitiveByteVariants : IDisposable
{
    private static readonly byte[][] Separators =
    [
        [], ":"u8.ToArray(), "-"u8.ToArray(), "_"u8.ToArray(), ","u8.ToArray(), " "u8.ToArray(),
        "  "u8.ToArray(), "   "u8.ToArray(), "\t"u8.ToArray(), "\r"u8.ToArray(), "\n"u8.ToArray(),
        ", "u8.ToArray(), ",  "u8.ToArray(), ",   "u8.ToArray(), ": "u8.ToArray(), "- "u8.ToArray(),
        "_ "u8.ToArray()
    ];

    private readonly List<SensitiveVariant> _variants = [];
    private bool _disposed;

    public SensitiveByteVariants(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty) return;
        AddSecretBase(key, key.Length is 67 or 99 && IsSqlLiteral(key));
        var decoded = TryDecodeSqlLiteral(key);
        if (decoded is null) return;
        try { AddSecretBase(decoded, literal: false); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        _variants.Sort(static (left, right) => right.Bytes.Length.CompareTo(left.Bytes.Length));
    }

    public int MaximumLength => _variants.Count == 0 ? 0 : _variants[0].Bytes.Length;

    public bool TryMatchAt(ReadOnlySpan<byte> value, int start, out int consumed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var variant in _variants)
        {
            if (value.Length - start < variant.Bytes.Length) continue;
            var candidate = value.Slice(start, variant.Bytes.Length);
            if (variant.AsciiCaseInsensitive ? EqualsAsciiIgnoreCase(candidate, variant.Bytes) :
                candidate.SequenceEqual(variant.Bytes))
            {
                consumed = variant.Bytes.Length;
                return true;
            }
        }
        consumed = 0;
        return false;
    }

    private void AddSecretBase(ReadOnlySpan<byte> secret, bool literal)
    {
        Add(secret.ToArray(), asciiCaseInsensitive: literal);
        foreach (var separator in Separators)
        {
            Add(BuildHex(secret, separator, perBytePrefix: false, quote: 0), asciiCaseInsensitive: true);
            Add(BuildHex(secret, separator, perBytePrefix: true, quote: 0), asciiCaseInsensitive: true);
        }
        Add(BuildHex(secret, [], perBytePrefix: false, quote: (byte)'\''), asciiCaseInsensitive: true);
        Add(BuildHex(secret, [], perBytePrefix: false, quote: (byte)'"'), asciiCaseInsensitive: true);
    }

    private void Add(byte[] bytes, bool asciiCaseInsensitive)
    {
        foreach (var existing in _variants)
        {
            if (existing.AsciiCaseInsensitive == asciiCaseInsensitive && existing.Bytes.AsSpan().SequenceEqual(bytes))
            {
                CryptographicOperations.ZeroMemory(bytes);
                return;
            }
        }
        _variants.Add(new SensitiveVariant(bytes, asciiCaseInsensitive));
        _variants.Sort(static (left, right) => right.Bytes.Length.CompareTo(left.Bytes.Length));
    }

    private static byte[] BuildHex(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> separator, bool perBytePrefix,
        byte quote)
    {
        var wrapperLength = quote == 0 ? 0 : 3;
        var byteLength = perBytePrefix ? 4 : 2;
        var length = checked(wrapperLength + secret.Length * byteLength +
                             Math.Max(0, secret.Length - 1) * separator.Length);
        var result = new byte[length];
        var written = 0;
        if (quote != 0)
        {
            result[written++] = (byte)'x';
            result[written++] = quote;
        }
        const string hex = "0123456789ABCDEF";
        for (var index = 0; index < secret.Length; index++)
        {
            if (index > 0)
            {
                separator.CopyTo(result.AsSpan(written));
                written += separator.Length;
            }
            if (perBytePrefix)
            {
                result[written++] = (byte)'0';
                result[written++] = (byte)'x';
            }
            result[written++] = (byte)hex[secret[index] >> 4];
            result[written++] = (byte)hex[secret[index] & 0x0f];
        }
        if (quote != 0) result[written] = quote;
        return result;
    }

    private static bool IsSqlLiteral(ReadOnlySpan<byte> key) => key.Length >= 3 &&
        key[0] is (byte)'x' or (byte)'X' && key[1] == (byte)'\'' && key[^1] == (byte)'\'';

    private static byte[]? TryDecodeSqlLiteral(ReadOnlySpan<byte> key)
    {
        if (!IsSqlLiteral(key) || (key.Length - 3) % 2 != 0) return null;
        var decoded = new byte[(key.Length - 3) / 2];
        for (var index = 0; index < decoded.Length; index++)
        {
            var high = HexNibble(key[2 + index * 2]);
            var low = HexNibble(key[3 + index * 2]);
            if (high < 0 || low < 0)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return null;
            }
            decoded[index] = (byte)((high << 4) | low);
        }
        return decoded;
    }

    private static int HexNibble(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1
    };

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        for (var index = 0; index < left.Length; index++)
            if (ToLowerAscii(left[index]) != ToLowerAscii(right[index])) return false;
        return true;
    }

    private static byte ToLowerAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + ((byte)'a' - (byte)'A'))
        : value;

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var variant in _variants) CryptographicOperations.ZeroMemory(variant.Bytes);
        _variants.Clear();
        _disposed = true;
    }

    private sealed record SensitiveVariant(byte[] Bytes, bool AsciiCaseInsensitive);
}

internal sealed class StreamingByteKeyRedactor : IDisposable
{
    private readonly SensitiveByteVariants _variants;
    private byte[] _pending = [];
    private bool _completed;
    private bool _disposed;

    public StreamingByteKeyRedactor(ReadOnlySpan<byte> key) => _variants = new SensitiveByteVariants(key);

    public byte[] Append(ReadOnlySpan<byte> value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The redaction stream is already complete.");
        return Process(value, final: false);
    }

    public byte[] Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return [];
        _completed = true;
        return Process([], final: true);
    }

    private byte[] Process(ReadOnlySpan<byte> value, bool final)
    {
        if (_variants.MaximumLength == 0)
        {
            if (_pending.Length != 0) throw new InvalidOperationException();
            return value.ToArray();
        }

        var work = new byte[checked(_pending.Length + value.Length)];
        _pending.CopyTo(work, 0);
        value.CopyTo(work.AsSpan(_pending.Length));
        CryptographicOperations.ZeroMemory(_pending);
        _pending = [];
        var output = new byte[Math.Max(work.Length, ByteKeyRedactor.Replacement.Length)];
        var inputOffset = 0;
        var written = 0;
        try
        {
            while (inputOffset < work.Length)
            {
                if (_variants.TryMatchAt(work, inputOffset, out var consumed))
                {
                    EnsureCapacity(ref output, checked(written + ByteKeyRedactor.Replacement.Length));
                    ByteKeyRedactor.Replacement.CopyTo(output.AsSpan(written));
                    written += ByteKeyRedactor.Replacement.Length;
                    inputOffset += consumed;
                    continue;
                }

                if (!final && work.Length - inputOffset < _variants.MaximumLength) break;
                EnsureCapacity(ref output, checked(written + 1));
                output[written++] = work[inputOffset++];
            }

            if (inputOffset < work.Length) _pending = work.AsSpan(inputOffset).ToArray();
            return output.AsSpan(0, written).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(work);
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static void EnsureCapacity(ref byte[] value, int required)
    {
        if (required <= value.Length) return;
        var replacement = new byte[Math.Max(required, checked(value.Length * 2))];
        value.CopyTo(replacement, 0);
        CryptographicOperations.ZeroMemory(value);
        value = replacement;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _variants.Dispose();
        CryptographicOperations.ZeroMemory(_pending);
        _pending = [];
        _disposed = true;
    }
}

public interface ISqlCipherProcessRunner
{
    Task<SecureProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> standardInput, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey = default);
}

internal sealed class DefaultSqlCipherProcessRunner : ISqlCipherProcessRunner
{
    public Task<SecureProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> standardInput, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey = default) =>
        ProcessRunner.RunBytesAsync(fileName, arguments, standardInput, timeout, cancellationToken,
            outputRedactionKey: outputRedactionKey);
}

public sealed class SqlCipherVerifier
{
    private readonly ISqlCipherProcessRunner _runner;

    public SqlCipherVerifier() : this(null) { }

    public SqlCipherVerifier(ISqlCipherProcessRunner? runner)
    {
        _runner = runner ?? new DefaultSqlCipherProcessRunner();
    }
    public async Task<VerificationVerdict> VerifyAsync(string executable, string databasePath, byte[] key,
        int preferredCompatibility, int pageSize, string expectedVersion, CancellationToken cancellationToken)
    {
        var trials = new List<VerificationTrial>();
        var compatibilities = new[] { preferredCompatibility, 4, 3, 2, 1 }.Distinct().ToArray();
        foreach (var compatibility in compatibilities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var script = BuildVerificationScript(key, compatibility, pageSize);
            try
            {
                using var result = await _runner.RunAsync(executable, ["-batch", "-readonly", databasePath], script,
                    TimeSpan.FromMinutes(2), cancellationToken, key);
                var redactedOutput = ByteKeyRedactor.Redact(result.StandardOutput.Span, key);
                var redactedError = ByteKeyRedactor.Redact(result.StandardError.Span, key);
                try
                {
                    trials.Add(SqlCipherOutputParser.Parse(compatibility, pageSize, result.ExitCode,
                        redactedOutput, redactedError, result.TimedOut, result.StreamDrainTimedOut));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(redactedOutput);
                    CryptographicOperations.ZeroMemory(redactedError);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(script);
            }
        }
        return VerificationPolicy.Evaluate(trials, expectedVersion);
    }

    // Legacy key-literal helper. Production key-bearing paths use BuildKeyedScript instead.
    public static string BuildKeyLiteral(byte[] capturedKey)
    {
        ArgumentNullException.ThrowIfNull(capturedKey);
        if (capturedKey.Length is 67 or 99 && IsRawSqlLiteral(capturedKey))
            return Encoding.ASCII.GetString(capturedKey);
        if (LooksLikeMalformedLiteral(capturedKey))
            throw new InvalidDataException($"Unsupported SQLCipher key representation: {capturedKey.Length} bytes.");
        if (capturedKey.Length is 32 or 67 or 99)
            return $"x'{Convert.ToHexString(capturedKey).ToLowerInvariant()}'";
        throw new InvalidDataException($"Unsupported SQLCipher key representation: {capturedKey.Length} bytes.");
    }

    // Legacy string-returning surface. Internal validation uses BuildVerificationScript directly.
    public static string BuildScript(byte[] key, int compatibility, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(key);
        var script = BuildVerificationScript(key, compatibility, pageSize);
        try { return Encoding.UTF8.GetString(script); }
        finally { CryptographicOperations.ZeroMemory(script); }
    }

    internal static byte[] BuildVerificationScript(ReadOnlySpan<byte> key, int compatibility, int pageSize)
    {
        const string prefix = ".bail on\nPRAGMA key=\"";
        var suffix = $"\";\nPRAGMA cipher_compatibility={compatibility};\nPRAGMA cipher_page_size={pageSize};\n" +
                     "PRAGMA query_only=ON;\n" +
                     ".print __WAC_CIPHER__=\nPRAGMA cipher_version;\n" +
                     ".print __WAC_CIPHER_INTEGRITY__=\nPRAGMA cipher_integrity_check;\n" +
                     ".print __WAC_INTEGRITY__=\nPRAGMA integrity_check;\n" +
                     ".print __WAC_SCHEMA_COUNT__=\nSELECT count(*) FROM sqlite_master;\n";
        return BuildKeyedScript(key, prefix, suffix);
    }

    internal static byte[] BuildKeyedScript(ReadOnlySpan<byte> key, string prefix, string suffix)
    {
        if (key.Length is not (32 or 67 or 99))
            throw new InvalidDataException($"Unsupported SQLCipher key representation: {key.Length} bytes.");
        var isLiteral = IsRawSqlLiteral(key);
        if (!isLiteral && LooksLikeMalformedLiteral(key))
            throw new InvalidDataException($"Unsupported SQLCipher key representation: {key.Length} bytes.");

        var literalLength = isLiteral ? key.Length : checked(key.Length * 2 + 3);
        var result = new byte[checked(Encoding.UTF8.GetByteCount(prefix) + literalLength +
                                      Encoding.UTF8.GetByteCount(suffix))];
        var written = Encoding.UTF8.GetBytes(prefix.AsSpan(), result.AsSpan());
        if (isLiteral)
        {
            key.CopyTo(result.AsSpan(written));
            written += key.Length;
        }
        else
        {
            result[written++] = (byte)'x';
            result[written++] = (byte)'\'';
            const string hex = "0123456789abcdef";
            foreach (var value in key)
            {
                result[written++] = (byte)hex[value >> 4];
                result[written++] = (byte)hex[value & 0x0f];
            }
            result[written++] = (byte)'\'';
        }
        _ = Encoding.UTF8.GetBytes(suffix.AsSpan(), result.AsSpan(written));
        return result;
    }

    private static bool IsRawSqlLiteral(ReadOnlySpan<byte> key)
    {
        var expectedHex = key.Length switch { 67 => 64, 99 => 96, _ => -1 };
        if (expectedHex < 0 || key[0] is not ((byte)'x' or (byte)'X') || key[1] != (byte)'\'' ||
            key[^1] != (byte)'\'') return false;
        for (var i = 2; i < key.Length - 1; i++)
            if (!IsHex((char)key[i])) return false;
        return key.Length - 3 == expectedHex;
    }

    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool LooksLikeMalformedLiteral(ReadOnlySpan<byte> key) =>
        key.Length is 67 or 99 && key[0] is (byte)'x' or (byte)'X' && key[1] is (byte)'\'' or (byte)'"';

}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut,
    bool StreamDrainTimedOut = false);

public sealed class SecureProcessResult : IDisposable
{
    private readonly byte[] _standardOutput;
    private readonly byte[] _standardError;
    private bool _disposed;

    public SecureProcessResult(int exitCode, byte[] standardOutput, byte[] standardError, bool timedOut,
        bool streamDrainTimedOut = false)
    {
        ExitCode = exitCode;
        _standardOutput = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        _standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
        TimedOut = timedOut;
        StreamDrainTimedOut = streamDrainTimedOut;
    }

    public int ExitCode { get; }
    [JsonIgnore] public ReadOnlyMemory<byte> StandardOutput => _standardOutput;
    [JsonIgnore] public ReadOnlyMemory<byte> StandardError => _standardError;
    public bool TimedOut { get; }
    public bool StreamDrainTimedOut { get; }
    [JsonIgnore] public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_standardOutput);
        CryptographicOperations.ZeroMemory(_standardError);
        _disposed = true;
    }

    public override string ToString() =>
        $"SecureProcessResult {{ ExitCode = {ExitCode}, TimedOut = {TimedOut}, StreamDrainTimedOut = {StreamDrainTimedOut}, OutputBytes = {_standardOutput.Length}, ErrorBytes = {_standardError.Length}, IsDisposed = {IsDisposed} }}";
}

public static class ProcessRunner
{
    // Legacy string contract. Key-bearing production paths use RunPipelineBytesAsync directly.
    public static async Task<ProcessResult> RunPipelineAsync(string producerFileName,
        IReadOnlyList<string> producerArguments, string producerStandardInput, string consumerFileName,
        IReadOnlyList<string> consumerArguments, string consumerStandardInputPrefix,
        string consumerStandardInputSuffix, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producerStandardInput);
        ArgumentNullException.ThrowIfNull(consumerStandardInputPrefix);
        ArgumentNullException.ThrowIfNull(consumerStandardInputSuffix);
        var producerInput = Encoding.UTF8.GetBytes(producerStandardInput);
        var consumerPrefix = Encoding.UTF8.GetBytes(consumerStandardInputPrefix);
        var consumerSuffix = Encoding.UTF8.GetBytes(consumerStandardInputSuffix);
        try
        {
            using var result = await RunPipelineBytesAsync(producerFileName, producerArguments, producerInput,
                consumerFileName, consumerArguments, consumerPrefix, consumerSuffix, timeout, cancellationToken);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(producerInput);
            CryptographicOperations.ZeroMemory(consumerPrefix);
            CryptographicOperations.ZeroMemory(consumerSuffix);
        }
    }

    public static async Task<SecureProcessResult> RunPipelineBytesAsync(string producerFileName,
        IReadOnlyList<string> producerArguments, ReadOnlyMemory<byte> producerStandardInput, string consumerFileName,
        IReadOnlyList<string> consumerArguments, ReadOnlyMemory<byte> consumerStandardInputPrefix,
        ReadOnlyMemory<byte> consumerStandardInputSuffix,
        TimeSpan timeout, CancellationToken cancellationToken, ReadOnlyMemory<byte> outputRedactionKey = default,
        TimeSpan? streamDrainTimeout = null)
    {
        var producerInput = producerStandardInput.ToArray();
        var consumerPrefix = consumerStandardInputPrefix.ToArray();
        var consumerSuffix = consumerStandardInputSuffix.ToArray();
        var redactionKey = outputRedactionKey.ToArray();
        using var producer = StartRedirected(producerFileName, producerArguments, redirectInput: true);
        using var consumer = StartRedirected(consumerFileName, consumerArguments, redirectInput: true);
        var producerStarted = false;
        var consumerStarted = false;
        var returned = false;
        var timedOut = false;
        var streamDrainTimedOut = false;
        var drainTimeout = streamDrainTimeout ?? TimeSpan.FromSeconds(2);
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var producerError = new BoundedByteBuffer(2 * 1024 * 1024);
        using var consumerOutput = new BoundedByteBuffer(8 * 1024 * 1024);
        using var consumerError = new BoundedByteBuffer(2 * 1024 * 1024);
        Task producerStderr = Task.CompletedTask;
        Task consumerStdout = Task.CompletedTask;
        Task consumerStderr = Task.CompletedTask;
        Task transfer = Task.CompletedTask;
        try
        {
            if (!producer.Start()) throw new InvalidOperationException("Failed to start pipeline producer.");
            producerStarted = true;
            if (!consumer.Start()) throw new InvalidOperationException("Failed to start pipeline consumer.");
            consumerStarted = true;
            producerStderr = DrainBytesAsync(producer.StandardError.BaseStream, producerError, redactionKey,
                streamCts.Token);
            consumerStdout = DrainBytesAsync(consumer.StandardOutput.BaseStream, consumerOutput, redactionKey,
                streamCts.Token);
            consumerStderr = DrainBytesAsync(consumer.StandardError.BaseStream, consumerError, redactionKey,
                streamCts.Token);
            transfer = Task.Run(async () =>
            {
                try
                {
                    await consumer.StandardInput.BaseStream.WriteAsync(consumerPrefix, timeoutCts.Token);
                    await producer.StandardOutput.BaseStream.CopyToAsync(consumer.StandardInput.BaseStream,
                        timeoutCts.Token);
                    await consumer.StandardInput.BaseStream.WriteAsync(consumerSuffix, timeoutCts.Token);
                    await consumer.StandardInput.BaseStream.FlushAsync(timeoutCts.Token);
                }
                finally
                {
                    TryDisposeStandardInput(consumer);
                }
            }, timeoutCts.Token);
            try
            {
                await producer.StandardInput.BaseStream.WriteAsync(producerInput, timeoutCts.Token);
                await producer.StandardInput.BaseStream.FlushAsync(timeoutCts.Token);
                producer.StandardInput.Close();
                await Task.WhenAll(producer.WaitForExitAsync(timeoutCts.Token),
                    consumer.WaitForExitAsync(timeoutCts.Token), transfer.WaitAsync(timeoutCts.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryDisposeStandardInput(producer);
                TryDisposeStandardInput(consumer);
                await Task.WhenAll(TerminateAsync(producer, producerStarted, drainTimeout),
                    TerminateAsync(consumer, consumerStarted, drainTimeout));
            }
            if (timedOut)
            {
                var cleanup = await Task.WhenAll(
                    SettleCleanupTaskAsync(transfer, drainTimeout, CancelTransfer,
                        suppressCompletedFailures: true),
                    SettleCleanupTaskAsync(producerStderr, drainTimeout, CancelDrains,
                        suppressCompletedFailures: true),
                    SettleCleanupTaskAsync(consumerStdout, drainTimeout, CancelDrains,
                        suppressCompletedFailures: true),
                    SettleCleanupTaskAsync(consumerStderr, drainTimeout, CancelDrains,
                        suppressCompletedFailures: true));
                streamDrainTimedOut = cleanup.Any(static item => item);
            }
            else
            {
                await transfer;
                streamDrainTimedOut = await SettleDrainTasksAsync(
                    [producerStderr, consumerStdout, consumerStderr], drainTimeout, CancelDrains,
                    suppressCompletedFailures: false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var producerExitCode = TryGetExitCode(producer);
            var consumerExitCode = TryGetExitCode(consumer);
            var exitCode = producerExitCode != 0 ? producerExitCode : consumerExitCode;
            var output = consumerOutput.Take();
            var firstError = producerError.Take();
            var secondError = consumerError.Take();
            byte[] errors;
            try { errors = Combine(firstError, secondError); }
            finally
            {
                CryptographicOperations.ZeroMemory(firstError);
                CryptographicOperations.ZeroMemory(secondError);
            }
            returned = true;
            return new SecureProcessResult(exitCode, output, errors, timedOut, streamDrainTimedOut);
        }
        finally
        {
            try
            {
                if (!returned)
                {
                    TryDisposeStandardInput(producer);
                    TryDisposeStandardInput(consumer);
                    await Task.WhenAll(TerminateAsync(producer, producerStarted, drainTimeout),
                        TerminateAsync(consumer, consumerStarted, drainTimeout));
                    await Task.WhenAll(
                        SettleCleanupTaskAsync(transfer, drainTimeout, CancelTransfer,
                            suppressCompletedFailures: true),
                        SettleCleanupTaskAsync(producerStderr, drainTimeout, CancelDrains,
                            suppressCompletedFailures: true),
                        SettleCleanupTaskAsync(consumerStdout, drainTimeout, CancelDrains,
                            suppressCompletedFailures: true),
                        SettleCleanupTaskAsync(consumerStderr, drainTimeout, CancelDrains,
                            suppressCompletedFailures: true));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(producerInput);
                CryptographicOperations.ZeroMemory(consumerPrefix);
                CryptographicOperations.ZeroMemory(consumerSuffix);
                CryptographicOperations.ZeroMemory(redactionKey);
            }
        }

        void CancelDrains()
        {
            streamCts.Cancel();
            TryDisposeStandardError(producer);
            TryDisposeStandardOutput(consumer);
            TryDisposeStandardError(consumer);
        }

        void CancelTransfer()
        {
            timeoutCts.Cancel();
            TryDisposeStandardOutput(producer);
            TryDisposeStandardInput(consumer);
        }

        static Process StartRedirected(string fileName, IReadOnlyList<string> arguments, bool redirectInput)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = redirectInput,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            return process;
        }

        static byte[] Combine(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            if (first.IsEmpty) return second.ToArray();
            if (second.IsEmpty) return first.ToArray();
            var result = new byte[checked(first.Length + 1 + second.Length)];
            first.CopyTo(result);
            result[first.Length] = (byte)'\n';
            second.CopyTo(result.AsSpan(first.Length + 1));
            return result;
        }
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        string? standardInput, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? streamDrainTimeout = null)
    {
        var input = standardInput is null ? [] : Encoding.UTF8.GetBytes(standardInput);
        try
        {
            using var result = await RunBytesAsync(fileName, arguments, input, timeout, cancellationToken,
                workingDirectory, environment, streamDrainTimeout);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static async Task<SecureProcessResult> RunBytesAsync(string fileName, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> standardInput, TimeSpan timeout, CancellationToken cancellationToken,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? streamDrainTimeout = null, ReadOnlyMemory<byte> outputRedactionKey = default)
    {
        var input = standardInput.ToArray();
        var redactionKey = outputRedactionKey.ToArray();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? string.Empty
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var stdoutBytes = new BoundedByteBuffer(8 * 1024 * 1024);
        using var stderrBytes = new BoundedByteBuffer(2 * 1024 * 1024);
        var started = false;
        var returned = false;
        var timedOut = false;
        var streamDrainTimedOut = false;
        var drainTimeout = streamDrainTimeout ?? TimeSpan.FromSeconds(2);
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");
            started = true;
            stdout = DrainBytesAsync(process.StandardOutput.BaseStream, stdoutBytes, redactionKey, streamCts.Token);
            stderr = DrainBytesAsync(process.StandardError.BaseStream, stderrBytes, redactionKey, streamCts.Token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeoutCts.Token);
                await process.StandardInput.BaseStream.FlushAsync(timeoutCts.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryDisposeStandardInput(process);
                await TerminateAsync(process, started, drainTimeout);
            }

            if (timedOut)
            {
                var cleanup = await Task.WhenAll(
                    SettleCleanupTaskAsync(stdout, drainTimeout, CancelDrains, suppressCompletedFailures: true),
                    SettleCleanupTaskAsync(stderr, drainTimeout, CancelDrains, suppressCompletedFailures: true));
                streamDrainTimedOut = cleanup.Any(static item => item);
            }
            else
            {
                streamDrainTimedOut = await SettleDrainTasksAsync([stdout, stderr], drainTimeout, CancelDrains,
                    suppressCompletedFailures: false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new SecureProcessResult(TryGetExitCode(process), stdoutBytes.Take(), stderrBytes.Take(), timedOut,
                streamDrainTimedOut);
            returned = true;
            return result;
        }
        finally
        {
            try
            {
                if (!returned)
                {
                    TryDisposeStandardInput(process);
                    await TerminateAsync(process, started, drainTimeout);
                    await Task.WhenAll(
                        SettleCleanupTaskAsync(stdout, drainTimeout, CancelDrains,
                            suppressCompletedFailures: true),
                        SettleCleanupTaskAsync(stderr, drainTimeout, CancelDrains,
                            suppressCompletedFailures: true));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
                CryptographicOperations.ZeroMemory(redactionKey);
            }
        }

        void CancelDrains()
        {
            streamCts.Cancel();
            TryDisposeStandardOutput(process);
            TryDisposeStandardError(process);
        }
    }

    private static async Task DrainBytesAsync(Stream stream, BoundedByteBuffer destination,
        ReadOnlyMemory<byte> outputRedactionKey, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var redactor = new StreamingByteKeyRedactor(outputRedactionKey.Span);
        try
        {
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { return; }
                if (read == 0)
                {
                    var final = redactor.Complete();
                    try { destination.Append(final); }
                    finally { CryptographicOperations.ZeroMemory(final); }
                    return;
                }
                var redacted = redactor.Append(buffer.AsSpan(0, read));
                try { destination.Append(redacted); }
                finally { CryptographicOperations.ZeroMemory(redacted); }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task TerminateAsync(Process process, bool started, TimeSpan cleanupTimeout)
    {
        if (!started) return;
        var kill = Task.Run(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        _ = await SettleCleanupTaskAsync(kill, cleanupTimeout, static () => { },
            suppressCompletedFailures: true);

        Task wait;
        try
        {
            if (process.HasExited) return;
            wait = process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException) { return; }
        _ = await SettleCleanupTaskAsync(wait, cleanupTimeout, static () => { },
            suppressCompletedFailures: true);
    }

    private static int TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch (InvalidOperationException) { return -1; }
    }

    private static void TryDisposeStandardInput(Process process)
    {
        try { process.StandardInput.Dispose(); }
        catch (Exception) { }
    }

    private static void TryDisposeStandardOutput(Process process)
    {
        try { process.StandardOutput.Dispose(); }
        catch (Exception) { }
    }

    private static void TryDisposeStandardError(Process process)
    {
        try { process.StandardError.Dispose(); }
        catch (Exception) { }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }

    internal static async Task SettleDrainsAsync(Task stdout, Task stderr, bool suppressFailures)
    {
        if (suppressFailures)
        {
            await Task.WhenAll(IgnoreFailure(stdout), IgnoreFailure(stderr));
            return;
        }
        await Task.WhenAll(stdout, stderr);
    }

    internal static async Task<bool> SettleDrainTasksAsync(IReadOnlyList<Task> drains, TimeSpan timeout,
        Action cancel, bool suppressCompletedFailures)
    {
        var all = Task.WhenAll(drains);
        if (await Task.WhenAny(all, Task.Delay(timeout, CancellationToken.None)) != all)
        {
            cancel();
            ObserveFailure(all);
            var cleanup = Task.WhenAll(drains.Select(IgnoreFailure));
            if (await Task.WhenAny(cleanup, Task.Delay(timeout, CancellationToken.None)) == cleanup)
                await cleanup;
            else
                ObserveFailure(cleanup);
            return true;
        }
        if (suppressCompletedFailures) await IgnoreFailure(all);
        else await all;
        return false;

    }

    internal static async Task<bool> SettleCleanupTaskAsync(Task task, TimeSpan timeout, Action cancel,
        bool suppressCompletedFailures)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout, CancellationToken.None)) == task)
        {
            if (suppressCompletedFailures) await IgnoreFailure(task);
            else await task;
            return false;
        }

        cancel();
        ObserveFailure(task);
        var cleanup = IgnoreFailure(task);
        if (await Task.WhenAny(cleanup, Task.Delay(timeout, CancellationToken.None)) == cleanup)
            await cleanup;
        else
            ObserveFailure(cleanup);
        return true;
    }

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(static completed =>
        _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class BoundedByteBuffer(int capacity) : IDisposable
    {
        private readonly byte[] _bytes = new byte[capacity];
        private int _length;
        private bool _disposed;

        public void Append(ReadOnlySpan<byte> value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var length = Math.Min(value.Length, _bytes.Length - _length);
            if (length <= 0) return;
            value[..length].CopyTo(_bytes.AsSpan(_length));
            _length += length;
        }

        public byte[] Take()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = _bytes.AsSpan(0, _length).ToArray();
            CryptographicOperations.ZeroMemory(_bytes.AsSpan(0, _length));
            _length = 0;
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CryptographicOperations.ZeroMemory(_bytes);
            _length = 0;
            _disposed = true;
        }
    }

}
