using DesktopPet.Uninstaller.Core;
using System.Runtime.Versioning;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class ShortcutCleanupServiceTests
{
    [Fact]
    public void RemoveTargetShortcuts_removes_only_targets_in_installation()
    {
        var store = new FakeShortcutStore(
        [
            new(@"C:\Users\alice\Desktop\桌宠.lnk", @"C:\Pet\DesktopPet.Wpf.exe"),
            new(@"C:\Users\alice\Desktop\其他.lnk", @"C:\Other\DesktopPet.Wpf.exe")
        ]);

        new ShortcutCleanupService(store).RemoveTargetShortcuts(@"C:\Pet");

        Assert.Equal([@"C:\Users\alice\Desktop\桌宠.lnk"], store.Removed);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsShortcutStore_scans_nested_programs_directories()
    {
        var programsDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nestedDirectory = Path.Combine(programsDirectory, "Vendor", "DesktopPet");
        var shortcutPath = Path.Combine(nestedDirectory, "桌宠.lnk");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(shortcutPath, string.Empty);

        try
        {
            var store = new DesktopPet.Uninstaller.WindowsShortcutStore(
                () => [new(programsDirectory, SearchSubdirectories: true)],
                path => path == shortcutPath ? @"C:\Pet\DesktopPet.Wpf.exe" : null);

            var shortcut = Assert.Single(store.List());

            Assert.Equal(shortcutPath, shortcut.ShortcutPath);
        }
        finally
        {
            Directory.Delete(programsDirectory, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsShortcutStore_surfaces_unreadable_shortcut_target()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "桌宠.lnk"), string.Empty);

        try
        {
            var store = new DesktopPet.Uninstaller.WindowsShortcutStore(
                () => [new(directory, SearchSubdirectories: false)],
                _ => throw new InvalidOperationException("COM target read failed"));

            Assert.Throws<InvalidOperationException>(() => store.List().ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeShortcutStore(IEnumerable<ShortcutEntry> entries) : IShortcutStore
    {
        private readonly List<ShortcutEntry> entries = [.. entries];

        public List<string> Removed { get; } = [];

        public IEnumerable<ShortcutEntry> List() => entries;

        public void Delete(string shortcutPath)
        {
            Removed.Add(shortcutPath);
            entries.RemoveAll(entry => entry.ShortcutPath == shortcutPath);
        }
    }
}
