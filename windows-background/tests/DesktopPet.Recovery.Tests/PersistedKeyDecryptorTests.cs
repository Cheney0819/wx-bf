using System.Buffers.Binary;
using System.Security.Cryptography;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using Wx411.Core;

namespace DesktopPet.Recovery.Tests;

public sealed class PersistedKeyDecryptorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-persisted-key-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoredKeyDecryptsGenerationWithoutCapture()
    {
        var fixture = await CreateFixtureAsync();
        var databasePath = CopyEncryptedFixture("message_0.db");
        var source = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);
        var key = CorrectKey();
        fixture.Vault.Store(Metadata(databasePath), key);

        var observation = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [source],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.True(observation.HasValidatedKey);
        var output = Assert.Single(observation.OutputPaths);
        SqliteIntegrityChecker.VerifyFile(output);
        Assert.Equal(RecoveryActionKind.PublishOutputs,
            (await fixture.Machine.ObserveAsync(
                fixture.Epoch.Id,
                observation.Observation,
                default)).Kind);
        Assert.Equal(0,
            (await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default))!.RestartCount);
    }

    [Fact]
    public async Task WrongStoredKeyDoesNotClaimValidationOrOutput()
    {
        var fixture = await CreateFixtureAsync();
        var databasePath = CopyEncryptedFixture("message_0.db");
        var source = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);
        fixture.Vault.Store(Metadata(databasePath), Enumerable.Repeat((byte)0xCC, 32).ToArray());

        var observation = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [source],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.False(observation.HasValidatedKey);
        Assert.Empty(observation.OutputPaths);
        Assert.Equal("persisted_key_no_match", observation.FailureCode);
    }

    [Fact]
    public async Task OneUnreadableDatabaseDoesNotBlockAnother()
    {
        var fixture = await CreateFixtureAsync();
        var goodPath = CopyEncryptedFixture("message_0.db");
        var badPath = Path.Combine(
            _root,
            "db_storage",
            "message",
            "message_1.db");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllBytesAsync(badPath, new byte[32]);
        fixture.Vault.Store(Metadata(goodPath), CorrectKey());

        var observation = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [
                new DatabaseSource(badPath, new FileInfo(badPath).Length),
                new DatabaseSource(goodPath, new FileInfo(goodPath).Length),
            ],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.True(observation.HasValidatedKey);
        Assert.Single(observation.OutputPaths);
        Assert.Equal("persisted_key_partial_failure", observation.FailureCode);
        Assert.Single(observation.UnresolvedRequiredDatabases);
        Assert.Equal(badPath, observation.UnresolvedRequiredDatabases[0].Path);
    }

    [Fact]
    public async Task AuxiliaryFailureDoesNotRemainRestartEligible()
    {
        var fixture = await CreateFixtureAsync();
        var requiredPath = CopyEncryptedFixture(
            Path.Combine("db_storage", "message", "message_0.db"));
        var auxiliaryPath = Path.Combine(
            _root,
            "db_storage",
            "contact",
            "contact.db");
        Directory.CreateDirectory(Path.GetDirectoryName(auxiliaryPath)!);
        await File.WriteAllBytesAsync(auxiliaryPath, new byte[32]);
        fixture.Vault.Store(Metadata(requiredPath), CorrectKey());

        var result = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [
                new DatabaseSource(
                    auxiliaryPath,
                    new FileInfo(auxiliaryPath).Length),
                new DatabaseSource(
                    requiredPath,
                    new FileInfo(requiredPath).Length),
            ],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.True(result.HasValidatedKey);
        Assert.Single(result.OutputPaths);
        Assert.Empty(result.UnresolvedRequiredDatabases);
    }

    [Fact]
    public async Task CorruptVaultRecordIsQuarantinedBeforeLaterValidKeyIsTried()
    {
        var fixture = await CreateFixtureAsync();
        var databasePath = CopyEncryptedFixture("message_0.db");
        var source = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);
        fixture.Vault.Store(Metadata(databasePath), CorrectKey());
        var corruptId = new string('0', 64);
        await WriteMalformedJsonEnvelopeAsync(corruptId);

        var observation = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [source],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.True(observation.HasValidatedKey);
        Assert.Single(observation.OutputPaths);
        Assert.False(File.Exists(Path.Combine(_root, "vault", corruptId + ".vkey")));
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(_root, "vault"),
            "*.quarantine",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task VaultRecordsAreUnsealedOncePerRecoveryPass()
    {
        var protector = new CountingTestProtector();
        var fixture = await CreateFixtureAsync(protector);
        var firstPath = CopyEncryptedFixture("message_0.db");
        var secondPath = CopyEncryptedFixture("message_1.db");
        var wrongKey = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        fixture.Vault.Store(Metadata(firstPath), wrongKey);
        fixture.Vault.Store(Metadata(secondPath), wrongKey);

        var result = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch,
            _root,
            [
                new DatabaseSource(firstPath, new FileInfo(firstPath).Length),
                new DatabaseSource(secondPath, new FileInfo(secondPath).Length),
            ],
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            default);

        Assert.False(result.HasValidatedKey);
        Assert.Equal(2, protector.UnprotectCount);
    }

    private async Task WriteMalformedJsonEnvelopeAsync(string id)
    {
        var metadata = "{"u8.ToArray();
        var plaintext = new byte[8 + metadata.Length + 32];
        BinaryPrimitives.WriteInt32LittleEndian(plaintext, metadata.Length);
        metadata.CopyTo(plaintext.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(
            plaintext.AsSpan(4 + metadata.Length),
            32);
        CorrectKey().CopyTo(plaintext.AsSpan(8 + metadata.Length));
        var ciphertext = plaintext.ToArray();
        for (var index = 0; index < ciphertext.Length; index++)
            ciphertext[index] ^= 0x7D;

        var envelope = new byte[12 + ciphertext.Length];
        "DPKV"u8.CopyTo(envelope);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            envelope.AsSpan(8),
            ciphertext.Length);
        ciphertext.CopyTo(envelope.AsSpan(12));
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(_root, "vault", id + ".vkey"),
                envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    [Fact]
    public async Task CompletedGenerationIsIdempotentAcrossWorkerRuns()
    {
        var fixture = await CreateFixtureAsync();
        var databasePath = CopyEncryptedFixture("message_0.db");
        var source = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);
        fixture.Vault.Store(Metadata(databasePath), CorrectKey());
        var outputRoot = Path.Combine(_root, "output");

        var first = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch, _root, [source], outputRoot,
            new Progress<RecoveryProgress>(), default);
        var second = await fixture.Decryptor.TryDecryptAsync(
            fixture.Epoch, _root, [source], outputRoot,
            new Progress<RecoveryProgress>(), default);

        Assert.Single(first.OutputPaths);
        Assert.Equal(first.OutputPaths, second.OutputPaths);
        Assert.True(second.HasValidatedKey);
        Assert.Single(Directory.EnumerateFiles(outputRoot, "*.sqlite"));
    }

    [Fact]
    public async Task RecoveryAdapterDiscoversKnownRootAndReusesKey()
    {
        var fixture = await CreateFixtureAsync();
        var databasePath = CopyEncryptedFixture("message_2.db");
        fixture.Vault.Store(Metadata(databasePath), CorrectKey());
        var adapter = new PersistedKeyRecoveryAdapter(
            fixture.Decryptor,
            _root,
            Path.Combine(_root, "adapter-output"),
            new Progress<RecoveryProgress>());

        var observation = await adapter.TryDecryptAsync(fixture.Epoch, default);

        Assert.True(observation.HasValidatedKey);
        Assert.Single(observation.Databases);
        Assert.Single(observation.OutputPaths);
    }

    private async Task<PersistedKeyFixture> CreateFixtureAsync(
        ISecretProtector? protector = null)
    {
        var repository = new RecoveryRepository(
            Path.Combine(_root, "state", "recovery.db"),
            TimeProvider.System);
        await repository.InitializeAsync(default);
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);
        var vault = new ValidatedKeyVault(
            Path.Combine(_root, "vault"),
            protector ?? new XorTestProtector());
        return new PersistedKeyFixture(
            repository,
            epoch,
            vault,
            new PersistedKeyDecryptor(repository, vault),
            new RecoveryStateMachine(repository));
    }

    private string CopyEncryptedFixture(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), path);
        return path;
    }

    private static byte[] CorrectKey() => Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private static ValidatedDatabaseKeyMetadata Metadata(string path) =>
        new(
            path,
            new DatabaseFileGeneration(4096, DateTime.UnixEpoch, "original"),
            SqlCipher4.Profile.Name,
            "sqlite3_key_equiv");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record PersistedKeyFixture(
        RecoveryRepository Repository,
        RecoveryEpoch Epoch,
        ValidatedKeyVault Vault,
        PersistedKeyDecryptor Decryptor,
        RecoveryStateMachine Machine);

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            var result = input.ToArray();
            for (var index = 0; index < result.Length; index++) result[index] ^= 0x7D;
            return result;
        }
    }

    private sealed class CountingTestProtector : ISecretProtector
    {
        public int UnprotectCount { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            UnprotectCount++;
            return Transform(ciphertext);
        }

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            var result = input.ToArray();
            for (var index = 0; index < result.Length; index++) result[index] ^= 0x7D;
            return result;
        }
    }
}
