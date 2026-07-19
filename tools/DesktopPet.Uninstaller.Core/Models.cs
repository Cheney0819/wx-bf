namespace DesktopPet.Uninstaller.Core;

public enum InstallKind
{
    InnoSetup,
    Direct
}

public sealed record InstallationCandidate(
    string InstallDirectory,
    InstallKind Kind,
    string? UninstallCommand);

public sealed record OperationResult(bool Succeeded, IReadOnlyList<string> Messages)
{
    public static OperationResult Success(params string[] messages) => new(true, messages);

    public static OperationResult Failure(params string[] messages) => new(false, messages);
}
