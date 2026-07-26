using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record CipherProfileMatch(
    CipherProfile Profile,
    IReadOnlyList<int> VerifiedPages);

public sealed record CipherProfileCacheLookup(
    CipherProfileMatch? Match,
    bool CacheHit);

/// <summary>
/// Positive and negative probe cache for one stable database operation.
/// Keys are indexed by SHA-256 digest; raw candidate bytes are never retained.
/// </summary>
public sealed class CipherProfileValidationCache : IDisposable
{
    private readonly byte[] _database;
    private readonly byte[] _salt;
    private readonly IReadOnlyList<CipherProfile> _profiles;
    private readonly Dictionary<string, CipherProfileMatch?> _matches =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public CipherProfileValidationCache(
        byte[] database,
        byte[] salt,
        IReadOnlyList<CipherProfile>? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != 16) throw new ArgumentException("Database salt must be 16 bytes.", nameof(salt));
        _database = database;
        _profiles = Array.AsReadOnly((profiles ?? CipherProfileProbe.CandidateProfilesFor(database.LongLength))
            .ToArray());
        _salt = salt.ToArray();
    }

    public int Count => _matches.Count;

    public CipherProfileCacheLookup FindMatch(
        ReadOnlySpan<byte> rawKey,
        CipherProfileProbeCounters counters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(counters);
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(rawKey, digest);
        var identity = Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        if (_matches.TryGetValue(identity, out var cached))
            return new CipherProfileCacheLookup(cached, CacheHit: true);

        var match = CipherProfileProbe.FindMatch(
            _database,
            rawKey,
            _salt,
            _profiles,
            cancellationToken,
            counters);
        _matches.Add(identity, match);
        return new CipherProfileCacheLookup(match, CacheHit: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_salt);
        _matches.Clear();
        _disposed = true;
    }
}

public sealed class CipherProfileProbeCounters
{
    public long ProfileAttempts { get; private set; }
    public long PageAuthentications { get; private set; }

    internal void RecordProfileAttempt() => ProfileAttempts = SaturatingIncrement(ProfileAttempts);

    internal void RecordPageAuthentication() =>
        PageAuthentications = SaturatingIncrement(PageAuthentications);

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;
}

public static class CipherProfileProbe
{
    private static readonly int[] PageSizes =
        { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };

    private static readonly AlgorithmChoice[] Algorithms =
    {
        new(HashAlgorithmName.SHA512, "512", 64, 80),
        new(HashAlgorithmName.SHA256, "256", 32, 48),
        new(HashAlgorithmName.SHA1, "1", 20, 48),
    };

    private static readonly IReadOnlyList<CipherProfile> Catalog =
        new ReadOnlyCollection<CipherProfile>(BuildDefaultProfiles());

    public static IReadOnlyList<CipherProfile> DefaultProfiles => Catalog;

    public static IReadOnlyList<CipherProfile> CandidateProfilesFor(long databaseLength)
    {
        if (databaseLength < 0) throw new ArgumentOutOfRangeException(nameof(databaseLength));
        var compatible = Catalog
            .Where(profile => databaseLength >= profile.PageSize && databaseLength % profile.PageSize == 0)
            .ToArray();
        return Array.AsReadOnly(compatible);
    }

    public static CipherProfileMatch? FindMatch(
        DatabaseProbeDescriptor descriptor,
        ReadOnlySpan<byte> rawKey,
        CancellationToken cancellationToken = default,
        CipherProfileProbeCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (rawKey.Length != 32) throw new ArgumentException("Raw key must be 32 bytes.", nameof(rawKey));
        var macKeys = new Dictionary<MacKeyCacheKey, byte[]>();
        try
        {
            foreach (var profile in descriptor.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counters?.RecordProfileAttempt();
                var pageCount = checked((int)(descriptor.Length / profile.PageSize));
                var samples = SelectSamplePages(pageCount);
                if (samples.Count < 2) continue;
                var cacheKey = new MacKeyCacheKey(
                    profile.HmacKdfAlgorithm.Name ?? string.Empty,
                    profile.HmacKdfIterations,
                    profile.SaltXor);
                if (!macKeys.TryGetValue(cacheKey, out var macKey))
                {
                    macKey = SqlCipher4.MakeMacKey(rawKey, descriptor.Salt, profile);
                    macKeys.Add(cacheKey, macKey);
                }

                var verifiedPages = new List<int>(2);
                for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                {
                    var pageNumber = samples[sampleIndex];
                    cancellationToken.ThrowIfCancellationRequested();
                    counters?.RecordPageAuthentication();
                    var page = descriptor.RequiredSample(profile.PageSize, pageNumber);
                    if (SqlCipher4.VerifyEncryptedPageWithMacKey(
                            page.Data,
                            macKey,
                            pageNumber,
                            profile))
                    {
                        verifiedPages.Add(pageNumber);
                        if (verifiedPages.Count == 2)
                            return CreateMatch(profile, verifiedPages);
                    }

                    var remaining = samples.Count - sampleIndex - 1;
                    if (verifiedPages.Count + remaining < 2) break;
                }
            }

            return null;
        }
        finally
        {
            foreach (var macKey in macKeys.Values) CryptographicOperations.ZeroMemory(macKey);
        }
    }

    public static CipherProfileMatch? FindMatch(
        ReadOnlySpan<byte> database,
        ReadOnlySpan<byte> rawKey,
        ReadOnlySpan<byte> salt,
        IReadOnlyList<CipherProfile>? profiles = null,
        CancellationToken cancellationToken = default,
        CipherProfileProbeCounters? counters = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rawKey.Length != 32) throw new ArgumentException("Raw key must be 32 bytes.", nameof(rawKey));
        if (salt.Length != 16) throw new ArgumentException("Database salt must be 16 bytes.", nameof(salt));
        profiles ??= CandidateProfilesFor(database.Length);

        var macKeys = new Dictionary<MacKeyCacheKey, byte[]>();
        try
        {
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SqlCipher4.ValidateProfile(profile);
                if (database.Length < profile.PageSize || database.Length % profile.PageSize != 0)
                    continue;

                counters?.RecordProfileAttempt();
                var pageCount = database.Length / profile.PageSize;
                var samples = SelectSamplePages(pageCount);
                if (samples.Count < 2) continue;
                const int requiredMatches = 2;
                var cacheKey = new MacKeyCacheKey(
                    profile.HmacKdfAlgorithm.Name ?? string.Empty,
                    profile.HmacKdfIterations,
                    profile.SaltXor);
                if (!macKeys.TryGetValue(cacheKey, out var macKey))
                {
                    macKey = SqlCipher4.MakeMacKey(rawKey, salt, profile);
                    macKeys.Add(cacheKey, macKey);
                }

                var verifiedPages = new List<int>(requiredMatches);
                counters?.RecordPageAuthentication();
                if (!SqlCipher4.VerifyPageWithMacKey(database, macKey, samples[0], profile))
                    continue;
                verifiedPages.Add(samples[0]);
                if (verifiedPages.Count >= requiredMatches)
                    return CreateMatch(profile, verifiedPages);

                for (var sampleIndex = 1; sampleIndex < samples.Count; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counters?.RecordPageAuthentication();
                    var pageNumber = samples[sampleIndex];
                    if (SqlCipher4.VerifyPageWithMacKey(database, macKey, pageNumber, profile))
                    {
                        verifiedPages.Add(pageNumber);
                        if (verifiedPages.Count >= requiredMatches)
                            return CreateMatch(profile, verifiedPages);
                    }

                    var remaining = samples.Count - sampleIndex - 1;
                    if (verifiedPages.Count + remaining < requiredMatches)
                        break;
                }
            }

            return null;
        }
        finally
        {
            foreach (var macKey in macKeys.Values) CryptographicOperations.ZeroMemory(macKey);
        }
    }

    internal static IReadOnlyList<int> SelectSamplePages(int pageCount)
    {
        if (pageCount <= 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var pages = new List<int>(4);
        AddUniquePage(pages, pageCount >= 2 ? 2 : 1, pageCount);
        AddUniquePage(pages, 1, pageCount);
        AddUniquePage(pages, pageCount / 2 + 1, pageCount);
        AddUniquePage(pages, pageCount, pageCount);
        return pages.ToArray();
    }

    private static CipherProfileMatch CreateMatch(
        CipherProfile profile,
        IReadOnlyCollection<int> verifiedPages) =>
        new(profile, Array.AsReadOnly(verifiedPages.ToArray()));

    private static void AddUniquePage(List<int> pages, int pageNumber, int pageCount)
    {
        if (pageNumber < 1 || pageNumber > pageCount || pages.Contains(pageNumber)) return;
        pages.Add(pageNumber);
    }

    private static CipherProfile[] BuildDefaultProfiles()
    {
        var profiles = new List<CipherProfile>(72) { SqlCipher4.Profile };
        var sha512 = Algorithms[0];

        foreach (var pageSize in PageSizes)
        {
            if (pageSize == SqlCipher4.Profile.PageSize) continue;
            profiles.Add(CreateProfile(pageSize, sha512, sha512));
        }

        foreach (var kdf in Algorithms)
        {
            foreach (var hmac in Algorithms)
            {
                if (kdf.Algorithm == HashAlgorithmName.SHA512 &&
                    hmac.Algorithm == HashAlgorithmName.SHA512) continue;
                profiles.Add(CreateProfile(SqlCipher4.Profile.PageSize, kdf, hmac));
            }
        }

        foreach (var pageSize in PageSizes)
        {
            if (pageSize == SqlCipher4.Profile.PageSize) continue;
            foreach (var kdf in Algorithms)
            {
                foreach (var hmac in Algorithms)
                {
                    if (kdf.Algorithm == HashAlgorithmName.SHA512 &&
                        hmac.Algorithm == HashAlgorithmName.SHA512) continue;
                    profiles.Add(CreateProfile(pageSize, kdf, hmac));
                }
            }
        }

        if (profiles.Count != 72)
            throw new InvalidOperationException($"Profile catalog construction produced {profiles.Count} entries.");
        return profiles.ToArray();
    }

    private static CipherProfile CreateProfile(
        int pageSize,
        AlgorithmChoice kdf,
        AlgorithmChoice hmac) => new(
        $"ps{pageSize}-kdf{kdf.ShortName}-hmac{hmac.ShortName}-le",
        PageSize: pageSize,
        Reserve: hmac.Reserve,
        HmacSize: hmac.DigestSize,
        HmacKdfIterations: 2,
        PassphraseKdfIterations: 256000,
        SaltXor: 0x3A,
        HmacAlgorithm: hmac.Algorithm,
        HmacKdfAlgorithm: kdf.Algorithm,
        PageNumberEncoding: PageNumberEncoding.LittleEndian);

    private sealed record AlgorithmChoice(
        HashAlgorithmName Algorithm,
        string ShortName,
        int DigestSize,
        int Reserve);

    private readonly record struct MacKeyCacheKey(
        string Algorithm,
        int Iterations,
        byte SaltXor);
}
