using System.Security.Cryptography;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class CipherProfileProbeTests
{
    [Fact]
    public void DefaultCatalogContainsSeventyTwoUniqueProfilesInEvidenceOrder()
    {
        var profiles = CipherProfileProbe.DefaultProfiles;

        Assert.Equal(72, profiles.Count);
        Assert.Equal(SqlCipher4.Profile, profiles[0]);
        Assert.Equal(72, profiles.Select(profile => profile.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(72, profiles.Select(ProfileIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.All(profiles, profile =>
        {
            Assert.Equal(PageNumberEncoding.LittleEndian, profile.PageNumberEncoding);
            Assert.Equal(profile.HmacAlgorithm == HashAlgorithmName.SHA512 ? 80 : 48, profile.Reserve);
            Assert.Equal(profile.HmacAlgorithm == HashAlgorithmName.SHA512
                ? 64
                : profile.HmacAlgorithm == HashAlgorithmName.SHA256 ? 32 : 20, profile.HmacSize);
        });

        Assert.All(profiles.Skip(1).Take(7), profile =>
        {
            Assert.NotEqual(4096, profile.PageSize);
            Assert.Equal(HashAlgorithmName.SHA512, profile.HmacKdfAlgorithm);
            Assert.Equal(HashAlgorithmName.SHA512, profile.HmacAlgorithm);
        });
    }

    [Fact]
    public void CandidateProfilesSkipPageSizesThatCannotFitOrDivideDatabase()
    {
        var profiles = CipherProfileProbe.CandidateProfilesFor(4L * 4096);

        Assert.Equal(54, profiles.Count);
        Assert.All(profiles, profile =>
        {
            Assert.True(profile.PageSize <= 4 * 4096);
            Assert.Equal(0, (4 * 4096) % profile.PageSize);
        });
        Assert.DoesNotContain(profiles, profile => profile.PageSize is 32768 or 65536);
    }

    [Fact]
    public void SamplePagesPreferOrdinaryPageThenFirstMiddleAndLastWithoutDuplicates()
    {
        Assert.Equal(new[] { 1 }, CipherProfileProbe.SelectSamplePages(1));
        Assert.Equal(new[] { 2, 1 }, CipherProfileProbe.SelectSamplePages(2));
        Assert.Equal(new[] { 2, 1, 3 }, CipherProfileProbe.SelectSamplePages(3));
        Assert.Equal(new[] { 2, 1, 3, 4 }, CipherProfileProbe.SelectSamplePages(4));
        Assert.Equal(new[] { 2, 1, 3, 5 }, CipherProfileProbe.SelectSamplePages(5));
    }

    [Fact]
    public void EveryCatalogProfileCanBeMatchedExactly()
    {
        foreach (var profile in CipherProfileProbe.DefaultProfiles)
        {
            var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);

            var match = CipherProfileProbe.FindMatch(
                fixture.Encrypted,
                fixture.Key,
                fixture.Salt,
                profiles: new[] { profile });

            Assert.NotNull(match);
            Assert.Equal(profile, match!.Profile);
            Assert.True(match.VerifiedPages.Count >= 2, profile.Name);
            Assert.Equal(2, match.VerifiedPages[0]);
        }
    }

    [Fact]
    public void MatchCanUseTwoOrdinaryPagesWhenFirstPageTagIsDamaged()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var damaged = CipherFixtureFactory.CorruptPageTag(fixture.Encrypted, profile, pageNumber: 1);

        var match = CipherProfileProbe.FindMatch(
            damaged,
            fixture.Key,
            fixture.Salt,
            profiles: new[] { profile });

        Assert.NotNull(match);
        Assert.Equal(new[] { 2, 3 }, match!.VerifiedPages);
    }

    [Fact]
    public void MatchRejectsDatabaseWhenOnlyOneSamplePageAuthenticates()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var damaged = CipherFixtureFactory.CorruptPageTag(fixture.Encrypted, profile, pageNumber: 1);
        damaged = CipherFixtureFactory.CorruptPageTag(damaged, profile, pageNumber: 3);
        damaged = CipherFixtureFactory.CorruptPageTag(damaged, profile, pageNumber: 4);

        var match = CipherProfileProbe.FindMatch(
            damaged,
            fixture.Key,
            fixture.Salt,
            profiles: new[] { profile });

        Assert.Null(match);
    }

    [Fact]
    public void SinglePageDatabaseDoesNotSatisfyTwoPageAcceptanceRule()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 1);

        var match = CipherProfileProbe.FindMatch(
            fixture.Encrypted,
            fixture.Key,
            fixture.Salt,
            profiles: new[] { profile });

        Assert.Null(match);
    }

    [Fact]
    public void WrongKeyReturnsNoProfileAndRecordsBoundedWork()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var wrong = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        var counters = new CipherProfileProbeCounters();

        var match = CipherProfileProbe.FindMatch(
            fixture.Encrypted,
            wrong,
            fixture.Salt,
            profiles: new[] { profile },
            counters: counters);

        Assert.Null(match);
        Assert.Equal(1, counters.ProfileAttempts);
        Assert.Equal(1, counters.PageAuthentications);
    }

    [Fact]
    public void CancelledProbeStopsBeforeCryptographicWork()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 4);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => CipherProfileProbe.FindMatch(
            fixture.Encrypted,
            fixture.Key,
            fixture.Salt,
            cancellationToken: source.Token));
    }

    [Fact]
    public void ValidationCacheReusesPositiveAndNegativeResultsWithoutRepeatingProfiles()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var wrong = Enumerable.Repeat((byte)0x7A, 32).ToArray();
        using var cache = new CipherProfileValidationCache(
            fixture.Encrypted,
            fixture.Salt,
            profiles: new[] { profile });
        var counters = new CipherProfileProbeCounters();

        var firstMiss = cache.FindMatch(
            wrong,
            counters);
        var attemptsAfterMiss = counters.ProfileAttempts;
        var secondMiss = cache.FindMatch(
            wrong,
            counters);
        var firstHit = cache.FindMatch(
            fixture.Key,
            counters);
        var attemptsAfterHit = counters.ProfileAttempts;
        var secondHit = cache.FindMatch(
            fixture.Key,
            counters);

        Assert.Null(firstMiss.Match);
        Assert.False(firstMiss.CacheHit);
        Assert.Null(secondMiss.Match);
        Assert.True(secondMiss.CacheHit);
        Assert.Equal(attemptsAfterMiss, attemptsAfterHit - 1);
        Assert.NotNull(firstHit.Match);
        Assert.False(firstHit.CacheHit);
        Assert.Equal(firstHit.Match, secondHit.Match);
        Assert.True(secondHit.CacheHit);
        Assert.Equal(attemptsAfterHit, counters.ProfileAttempts);
        Assert.Equal(2, cache.Count);
    }

    private static string ProfileIdentity(CipherProfile profile) => string.Join(
        ':',
        profile.PageSize,
        profile.Reserve,
        profile.HmacSize,
        profile.HmacKdfIterations,
        profile.HmacKdfAlgorithm.Name,
        profile.HmacAlgorithm.Name,
        profile.PageNumberEncoding);
}
