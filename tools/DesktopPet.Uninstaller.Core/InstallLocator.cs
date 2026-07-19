namespace DesktopPet.Uninstaller.Core;

public interface IInstallationStore
{
    IEnumerable<InstallationCandidate> ReadInnoCandidates();

    IEnumerable<string> ReadLegacyDirectories();
}

public sealed class InstallLocator(IInstallationStore store, Func<string, bool> isVerifiedInstallation)
{
    public IReadOnlyList<InstallationCandidate> Locate(string? commandLineDirectory)
    {
        var innoCandidates = store.ReadInnoCandidates()
            .Where(candidate => IsVerified(candidate.InstallDirectory))
            .DistinctBy(candidate => candidate.InstallDirectory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (IsVerified(commandLineDirectory))
        {
            var innoCandidate = innoCandidates.FirstOrDefault(candidate =>
                candidate.InstallDirectory.Equals(commandLineDirectory, StringComparison.OrdinalIgnoreCase));
            return [innoCandidate ?? new InstallationCandidate(commandLineDirectory!, InstallKind.Direct, null)];
        }

        if (innoCandidates.Length > 0)
        {
            return innoCandidates;
        }

        return store.ReadLegacyDirectories()
            .Where(IsVerified)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(directory => new InstallationCandidate(directory, InstallKind.Direct, null))
            .ToArray();
    }

    private bool IsVerified(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && isVerifiedInstallation(directory);
}
