using System.Text;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.Background.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-atomic-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplaceNeverLeavesTemporaryFile()
    {
        var destination = Path.Combine(_root, "state.bin");

        await AtomicFile.ReplaceAsync(destination, "one"u8.ToArray(), default);
        await AtomicFile.ReplaceAsync(destination, "two"u8.ToArray(), default);

        Assert.Equal("two", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ReplaceCreatesDestinationDirectory()
    {
        var destination = Path.Combine(_root, "nested", "state.bin");

        await AtomicFile.ReplaceAsync(destination, Encoding.UTF8.GetBytes("ready"), default);

        Assert.Equal("ready", await File.ReadAllTextAsync(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
