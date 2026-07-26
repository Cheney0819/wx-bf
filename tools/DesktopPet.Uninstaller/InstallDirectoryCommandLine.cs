namespace DesktopPet.Uninstaller;

public enum InstallDirectoryArgumentState
{
    Absent,
    Valid,
    Malformed
}

public sealed record InstallDirectoryArgument(InstallDirectoryArgumentState State, string? Directory)
{
    public static InstallDirectoryArgument Absent { get; } = new(InstallDirectoryArgumentState.Absent, null);
}

public static class InstallDirectoryCommandLine
{
    public static InstallDirectoryArgument Parse(IReadOnlyList<string> arguments)
    {
        string? directory = null;
        var found = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--install-dir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            if (index + 1 >= arguments.Count ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return new InstallDirectoryArgument(InstallDirectoryArgumentState.Malformed, null);
            }

            directory = arguments[++index];
        }

        return found
            ? new InstallDirectoryArgument(InstallDirectoryArgumentState.Valid, directory)
            : InstallDirectoryArgument.Absent;
    }
}
