using System.Security.Cryptography;

namespace Wx411.Core.Tests;

public sealed class MultiDatabaseCaptureCollectorTests
{
    [Fact]
    public void SampleMatchRemainsPendingUntilExporterConfirmsIt()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        using var probe = Probe(fixture.Encrypted);
        using var collector = new MultiDatabaseCaptureCollector([probe.Descriptor]);
        using var candidate = Candidate(fixture.Key);

        var update = collector.TryCollect(candidate, new CipherProfileProbeCounters());

        var match = Assert.Single(update.NewMatches);
        Assert.Empty(collector.Matches);
        Assert.Equal([probe.Descriptor.Path], collector.PendingDatabaseIds);
        var key = collector.CopyPendingKey(match);
        try
        {
            Assert.Equal(fixture.Key, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        collector.ConfirmExport(match);
        Assert.Same(match, Assert.Single(collector.Matches));
        Assert.Empty(collector.PendingDatabaseIds);
    }

    [Fact]
    public void OneCandidateCanMatchTwoDescriptors()
    {
        var first = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var second = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8, saltOffset: 17);
        using var firstProbe = Probe(first.Encrypted);
        using var secondProbe = Probe(second.Encrypted);
        using var collector = new MultiDatabaseCaptureCollector(
            [firstProbe.Descriptor, secondProbe.Descriptor]);
        using var candidate = Candidate(first.Key);

        var update = collector.TryCollect(candidate, new CipherProfileProbeCounters());

        Assert.Equal(2, update.NewMatches.Count);
        Assert.True(update.IsComplete);
        Assert.Equal(2, collector.PendingMatches.Count);
    }

    [Fact]
    public void SynchronizeAddsLoginTimeDatabaseForLaterCandidate()
    {
        var first = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var second = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8, keyOffset: 29, saltOffset: 17);
        using var firstProbe = Probe(first.Encrypted);
        using var secondProbe = Probe(second.Encrypted);
        using var collector = new MultiDatabaseCaptureCollector([firstProbe.Descriptor]);
        var counters = new CipherProfileProbeCounters();
        using var firstCandidate = Candidate(first.Key);
        using var secondCandidate = Candidate(second.Key);
        collector.TryCollect(firstCandidate, counters);

        collector.Synchronize([firstProbe.Descriptor, secondProbe.Descriptor], counters);
        var update = collector.TryCollect(secondCandidate, counters);

        Assert.Equal(secondProbe.Descriptor.Path, Assert.Single(update.NewMatches).DatabaseId);
        Assert.True(collector.IsReadyForValidation);
    }

    [Fact]
    public void ReleaseClearsPendingKey()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        using var probe = Probe(fixture.Encrypted);
        using var collector = new MultiDatabaseCaptureCollector([probe.Descriptor]);
        using var candidate = Candidate(fixture.Key);
        var match = Assert.Single(collector.TryCollect(
            candidate, new CipherProfileProbeCounters()).NewMatches);
        var key = collector.CopyPendingKey(match);

        collector.Release(match);

        Assert.Throws<ArgumentException>(() => collector.CopyPendingKey(match));
        CryptographicOperations.ZeroMemory(key);
    }

    private static ProbeOwner Probe(byte[] encrypted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wx411-probe-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(path, encrypted);
        return new ProbeOwner(path, DatabaseProbeDescriptor.Read(path));
    }

    private static CapturedKeyMaterial Candidate(byte[] key) => new(
        "test-callpoint", 0, string.Empty, 1, DateTime.UtcNow)
    {
        KeyData = key.ToArray(),
        KeyLength = key.Length,
    };

    private sealed class ProbeOwner(string path, DatabaseProbeDescriptor descriptor) : IDisposable
    {
        internal DatabaseProbeDescriptor Descriptor { get; } = descriptor;

        public void Dispose()
        {
            Descriptor.Dispose();
            File.Delete(path);
        }
    }
}
