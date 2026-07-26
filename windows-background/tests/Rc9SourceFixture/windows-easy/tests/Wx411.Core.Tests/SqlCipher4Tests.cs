using System.Text;
using System.Security.Cryptography;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class SqlCipher4Tests
{
    private static readonly byte[] RawKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static readonly byte[] Salt = Convert.FromHexString(
        "101112131415161718191a1b1c1d1e1f");

    [Fact]
    public void ProfileMatchesWeChatSqlCipher4Defaults()
    {
        Assert.Equal(4096, SqlCipher4.Profile.PageSize);
        Assert.Equal(80, SqlCipher4.Profile.Reserve);
        Assert.Equal(64, SqlCipher4.Profile.HmacSize);
        Assert.Equal(2, SqlCipher4.Profile.HmacKdfIterations);
        Assert.Equal(256000, SqlCipher4.Profile.PassphraseKdfIterations);
        Assert.Equal(0x3A, SqlCipher4.Profile.SaltXor);
        Assert.Equal(HashAlgorithmName.SHA512, SqlCipher4.Profile.HmacKdfAlgorithm);
        Assert.Equal(HashAlgorithmName.SHA512, SqlCipher4.Profile.HmacAlgorithm);
        Assert.Equal(PageNumberEncoding.LittleEndian, SqlCipher4.Profile.PageNumberEncoding);
    }

    [Fact]
    public void DeriveMacKeyMatchesDeterministicVector()
    {
        var actual = SqlCipher4.MakeMacKey(RawKey, Salt);
        Assert.Equal(
            "565fd7b4953a11b6e2faa5623aca48b690ad240ee0740db7346e8b1526ee524a",
            Convert.ToHexString(actual).ToLowerInvariant());
    }

    [Fact]
    public void DerivePassphraseMatchesDeterministicVector()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var actual = SqlCipher4.DeriveRawKey(Encoding.UTF8.GetBytes("fixture-passphrase"), salt);
        Assert.Equal(
            "8cb56c0cdab1b008f4ba6bbb00672136318bfb153df53ef6a78654718c08b454",
            Convert.ToHexString(actual).ToLowerInvariant());
    }

    [Fact]
    public void DecryptCheckedSqlCipherFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db");
        var encrypted = File.ReadAllBytes(path);
        var plain = SqlCipher4.DecryptDatabase(encrypted, RawKey);

        Assert.Equal(8192, plain.Length);
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(plain, 0, 16));
        Assert.Equal(80, plain[20]);
        Assert.Contains((byte)'h', plain);
        Assert.Contains((byte)'w', plain);
    }

    [Fact]
    public void WrongKeyFailsBeforeOutputCanBeAccepted()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db");
        var encrypted = File.ReadAllBytes(path);
        var wrong = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        Assert.ThrowsAny<IntegrityException>(() => SqlCipher4.DecryptDatabase(encrypted, wrong));
    }

    [Fact]
    public void FullAuthenticationReportsFailuresBeyondProbeSamplePages()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 240);
        var damaged = CipherFixtureFactory.CorruptPageTag(
            fixture.Encrypted,
            profile,
            pageNumber: 231);
        damaged = CipherFixtureFactory.CorruptPageTag(damaged, profile, pageNumber: 239);

        var probe = CipherProfileProbe.FindMatch(
            damaged,
            fixture.Key,
            fixture.Salt,
            profiles: new[] { profile });
        Assert.NotNull(probe);
        Assert.Equal(new[] { 2, 1 }, probe!.VerifiedPages);

        var report = SqlCipher4.AuthenticateDatabase(damaged, fixture.Key, profile);

        Assert.False(report.IsValid);
        Assert.Equal(240, report.PageCount);
        Assert.Equal(2, report.FailedPageCount);
        Assert.Equal(new[] { 231, 239 }, report.FailedPages);

        var error = Assert.Throws<PageAuthenticationException>(
            () => SqlCipher4.DecryptDatabase(damaged, fixture.Key, profile));
        Assert.Equal(report.PageCount, error.Report.PageCount);
        Assert.Equal(report.FailedPageCount, error.Report.FailedPageCount);
        Assert.Equal(report.FailedPages, error.Report.FailedPages);
    }

    [Fact]
    public void FullAuthenticationAcceptsPreallocatedZeroPagesAfterAuthenticatedPrefix()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var preallocated = fixture.Encrypted.ToArray();
        Array.Clear(preallocated, profile.PageSize * 2, profile.PageSize * 2);

        var report = SqlCipher4.AuthenticateDatabase(preallocated, fixture.Key, profile);

        Assert.True(report.IsValid);
        Assert.Equal(4, report.PageCount);
        Assert.Empty(report.FailedPages);
    }

    [Fact]
    public void DecryptDatabasePreservesPreallocatedZeroPages()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);
        var preallocated = fixture.Encrypted.ToArray();
        Array.Clear(preallocated, profile.PageSize * 2, profile.PageSize * 2);

        var plaintext = SqlCipher4.DecryptDatabase(preallocated, fixture.Key, profile);

        Assert.Equal(fixture.Plaintext.AsSpan(0, profile.PageSize * 2).ToArray(),
            plaintext.AsSpan(0, profile.PageSize * 2).ToArray());
        Assert.All(plaintext.AsSpan(profile.PageSize * 2).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ParseRawKeyLiteralChecksEmbeddedSalt()
    {
        var text = $"x'{Convert.ToHexString(RawKey)}{Convert.ToHexString(Salt)}'";
        Assert.Equal(RawKey, SqlCipher4.ParseKeyText(text, Salt));
        Assert.Throws<FormatException>(() => SqlCipher4.ParseKeyText(text, new byte[16]));
    }

    [Fact]
    public void VerifyPageSeparatesMacKdfFromPageHmacAndSupportsBigEndian()
    {
        var profile = new CipherProfile(
            "test-p4096-kdf256-hmac1-be",
            PageSize: 4096,
            Reserve: 48,
            HmacSize: 20,
            HmacKdfIterations: 2,
            PassphraseKdfIterations: 256000,
            SaltXor: 0x3A,
            HmacAlgorithm: HashAlgorithmName.SHA1,
            HmacKdfAlgorithm: HashAlgorithmName.SHA256,
            PageNumberEncoding: PageNumberEncoding.BigEndian);
        var fixture = CipherFixtureFactory.Create(profile);

        Assert.True(SqlCipher4.VerifyPage(
            fixture.Encrypted,
            fixture.Key,
            fixture.Salt,
            pageNumber: 2,
            profile));
        Assert.False(SqlCipher4.VerifyPage(
            fixture.Encrypted,
            fixture.Key,
            fixture.Salt,
            pageNumber: 2,
            SqlCipher4.Profile));
    }

    [Fact]
    public void DecryptDatabaseUsesNonDefaultProfileForEveryPage()
    {
        var profile = new CipherProfile(
            "test-p2048-kdf1-hmac256-le",
            PageSize: 2048,
            Reserve: 48,
            HmacSize: 32,
            HmacKdfIterations: 2,
            PassphraseKdfIterations: 256000,
            SaltXor: 0x3A,
            HmacAlgorithm: HashAlgorithmName.SHA256,
            HmacKdfAlgorithm: HashAlgorithmName.SHA1,
            PageNumberEncoding: PageNumberEncoding.LittleEndian);
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 4);

        var actual = SqlCipher4.DecryptDatabase(fixture.Encrypted, fixture.Key, profile);

        Assert.Equal(fixture.Plaintext, actual);
    }

    [Fact]
    public void IndependentNonDefaultVectorMatchesSha1KdfSha256HmacAnd2048Pages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sqlcipher_nondefault_independent.db");
        var encrypted = File.ReadAllBytes(path);
        var key = Convert.FromHexString(
            "f0e1d2c3b4a5968778695a4b3c2d1e0f" +
            "00112233445566778899aabbccddeeff");
        var salt = Convert.FromHexString("102132435465768798a9bacbdcedfe0f");
        var profile = CipherProfileProbe.DefaultProfiles.Single(item =>
            item.PageSize == 2048 &&
            item.HmacKdfAlgorithm == HashAlgorithmName.SHA1 &&
            item.HmacAlgorithm == HashAlgorithmName.SHA256);

        Assert.Equal(
            "3fece48c3efdb1c0c7cfefbafffa72bd228be2d0dcac96856a05cbfe3f072922",
            Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant());
        Assert.Equal(salt, encrypted[..16]);
        Assert.True(SqlCipher4.VerifyPage(encrypted, key, salt, pageNumber: 1, profile));
        Assert.True(SqlCipher4.VerifyPage(encrypted, key, salt, pageNumber: 2, profile));
        Assert.False(SqlCipher4.VerifyPage(encrypted, key, salt, pageNumber: 2, SqlCipher4.Profile));

        var match = CipherProfileProbe.FindMatch(
            encrypted,
            key,
            salt,
            profiles: new[] { profile });
        Assert.NotNull(match);
        Assert.Equal(new[] { 2, 1 }, match!.VerifiedPages);

        var plaintext = SqlCipher4.DecryptDatabase(encrypted, key, profile);
        Assert.Equal(
            "66d29d5d34eca640111100f8febc87abcc64043100e9751057d130d869cbba06",
            Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant());
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(plaintext, 0, 16));
    }
}
