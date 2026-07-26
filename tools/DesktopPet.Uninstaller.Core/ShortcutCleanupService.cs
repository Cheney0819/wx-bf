namespace DesktopPet.Uninstaller.Core;

public sealed record ShortcutEntry(string ShortcutPath, string TargetPath);

public interface IShortcutStore
{
    IEnumerable<ShortcutEntry> List();

    void Delete(string shortcutPath);
}

public sealed class ShortcutCleanupService(IShortcutStore store)
{
    public OperationResult RemoveTargetShortcuts(string installDirectory)
    {
        var removed = new List<string>();
        try
        {
            foreach (var shortcut in store.List().Where(shortcut =>
                         InstallPathPolicy.IsWithin(installDirectory, shortcut.TargetPath)))
            {
                store.Delete(shortcut.ShortcutPath);
                removed.Add(shortcut.ShortcutPath);
            }

            return OperationResult.Success(removed.ToArray());
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Failed to remove target shortcuts: {exception.Message}");
        }
    }
}
