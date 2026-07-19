using DesktopPet.Uninstaller.Core;
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
