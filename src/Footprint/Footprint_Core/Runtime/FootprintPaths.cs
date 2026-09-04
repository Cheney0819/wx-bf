namespace Footprint.Core.Runtime;

public sealed record FootprintPaths(
    string ProductRoot,
    string RuntimeDirectory,
    string StateDirectory,
    string LogsDirectory,
    string QueueDirectory,
    string PackagesDirectory,
    string MaintenanceLockPath)
{
    public static FootprintPaths ForCurrentUser() =>
        ForRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static FootprintPaths ForRoot(string localAppData)
    {
        var product = Path.GetFullPath(Path.Combine(localAppData, "Footprint"));
        return new FootprintPaths(
            product,
            Path.Combine(product, "Footprint_Runtime"),
            Path.Combine(product, "Footprint_State"),
            Path.Combine(product, "Footprint_Logs"),
            Path.Combine(product, "Footprint_Queue"),
            Path.Combine(product, "Footprint_Packages"),
            Path.Combine(product, "Footprint_Maintenance.lock"));
    }
}
