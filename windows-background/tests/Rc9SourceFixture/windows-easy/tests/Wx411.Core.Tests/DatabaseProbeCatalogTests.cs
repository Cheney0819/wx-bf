using System.Security.Cryptography;

namespace Wx411.Core.Tests;

public sealed class DatabaseProbeCatalogTests
{
    [Fact]
    public void DescriptorKeepsOnlySaltAndSelectedPages()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 240);
        var path = WriteTemp(fixture.Encrypted);
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(path);

            Assert.Equal(fixture.Encrypted.Length, descriptor.Length);
            Assert.Equal(fixture.Encrypted.AsSpan(0, 16).ToArray(), descriptor.Salt);
            Assert.Contains(descriptor.SamplePages,
                page => page.PageSize == SqlCipher4.Profile.PageSize && page.PageNumber == 2);
            Assert.Contains(descriptor.SamplePages,
                page => page.PageSize == SqlCipher4.Profile.PageSize && page.PageNumber == 1);
            Assert.Contains(descriptor.SamplePages,
                page => page.PageSize == SqlCipher4.Profile.PageSize && page.PageNumber == 121);
            Assert.Contains(descriptor.SamplePages,
                page => page.PageSize == SqlCipher4.Profile.PageSize && page.PageNumber == 240);
            Assert.True(descriptor.SamplePages.Sum(page => page.Data.Length) < descriptor.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DescriptorSamplesCanIdentifyTheCorrectProfileAndKey()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 240);
        var path = WriteTemp(fixture.Encrypted);
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(path);
            var counters = new CipherProfileProbeCounters();

            var match = CipherProfileProbe.FindMatch(
                descriptor,
                fixture.Key,
                cancellationToken: default,
                counters: counters);

            Assert.NotNull(match);
            Assert.Equal(SqlCipher4.Profile, match!.Profile);
            Assert.Equal([2, 1], match.VerifiedPages);
            Assert.True(counters.PageAuthentications >= 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RefreshAddsNewDatabaseAndReplacesChangedUnconfirmedGeneration()
    {
        var directory = Directory.CreateTempSubdirectory("wx411-probes-").FullName;
        var first = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var second = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 12, saltOffset: 17);
        var selectedPath = Path.Combine(directory, "message_0.db");
        var addedPath = Path.Combine(directory, "media_0.db");
        await File.WriteAllBytesAsync(selectedPath, first.Encrypted);
        try
        {
            using var catalog = DatabaseProbeCatalog.Create(selectedPath, [selectedPath]);
            var original = Assert.Single(catalog.Descriptors);
            await File.WriteAllBytesAsync(addedPath, first.Encrypted);
            await File.WriteAllBytesAsync(selectedPath, second.Encrypted);
            File.SetLastWriteTimeUtc(selectedPath, DateTime.UtcNow.AddSeconds(2));

            var update = await catalog.RefreshAsync();

            Assert.Contains(addedPath, update.AddedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(selectedPath, update.ReplacedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, catalog.Descriptors.Count);
            var replacement = catalog.Descriptors.Single(item =>
                string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(original.Generation, replacement.Generation);
            Assert.Equal(second.Encrypted.AsSpan(0, 16).ToArray(), replacement.Salt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmedDescriptorIsNotOverwrittenByRefresh()
    {
        var directory = Directory.CreateTempSubdirectory("wx411-probes-").FullName;
        var first = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var second = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 12, saltOffset: 17);
        var path = Path.Combine(directory, "message_0.db");
        await File.WriteAllBytesAsync(path, first.Encrypted);
        try
        {
            using var catalog = DatabaseProbeCatalog.Create(path, [path]);
            var original = Assert.Single(catalog.Descriptors);
            catalog.MarkConfirmed(path);
            await File.WriteAllBytesAsync(path, second.Encrypted);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

            var update = await catalog.RefreshAsync();

            Assert.Empty(update.ReplacedPaths);
            Assert.Same(original, Assert.Single(catalog.Descriptors));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectedDatabaseFailureStopsCatalogCreationButOtherFailureIsSkipped()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var selected = WriteTemp(fixture.Encrypted);
        var missing = selected + ".missing.db";
        try
        {
            using var catalog = DatabaseProbeCatalog.Create(selected, [selected, missing]);
            Assert.Equal([missing], catalog.SkippedPaths);
            Assert.Throws<FileNotFoundException>(() =>
                DatabaseProbeCatalog.Create(missing, [missing, selected]));
        }
        finally
        {
            File.Delete(selected);
        }
    }

    private static string WriteTemp(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wx411-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(path, data);
        return path;
    }
}
