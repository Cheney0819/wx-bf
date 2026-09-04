using Footprint.Core.Runtime;

namespace Footprint.Background;

public static class FootprintStartupGate
{
    public static bool CanStart(FootprintPaths paths) =>
        !File.Exists(paths.MaintenanceLockPath);
}
