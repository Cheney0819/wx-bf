using DesktopPet.Uninstaller.Core;
using System.IO;

namespace DesktopPet.Uninstaller;

public sealed record UninstallerSelfRelocationPlan(
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<string> Arguments);

public static class UninstallerSelfRelocator
{
    public const string RelocatedMarker = "--running-from-temp";
    public const string BootstrapProcessOption = "--wait-for-pid";

    public static UninstallerSelfRelocationPlan? CreatePlan(
        IReadOnlyList<string> arguments,
        string executablePath,
        string temporaryDirectory,
        IEnumerable<string> targetDirectories,
        int bootstrapProcessId)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The executable path is required.", nameof(executablePath));
        }

        if (!targetDirectories.Any(directory =>
                InstallPathPolicy.IsWithin(directory, executablePath)))
        {
            return null;
        }

        var fileName = executablePath.Replace('\\', '/').Split('/').Last();
        var destinationDirectory = Path.Combine(
            temporaryDirectory,
            "DesktopPet.Uninstaller",
            Guid.NewGuid().ToString("N"));
        return new UninstallerSelfRelocationPlan(
            executablePath,
            Path.Combine(destinationDirectory, fileName),
            [.. arguments, RelocatedMarker, BootstrapProcessOption, bootstrapProcessId.ToString()]);
    }

    public static int? ReadBootstrapProcessId(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(BootstrapProcessOption, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arguments[index + 1], out var processId) &&
                processId > 0)
            {
                return processId;
            }
        }

        return null;
    }
}
