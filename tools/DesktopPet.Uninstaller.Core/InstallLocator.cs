namespace DesktopPet.Uninstaller.Core;

public interface IInstallationStore
{
    IEnumerable<InstallationCandidate> ReadInnoCandidates();

    IEnumerable<string> ReadLegacyDirectories();
}

public sealed class InstallLocator(IInstallationStore store, Func<string, bool> isVerifiedInstallation)
{
    private readonly string profileDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IReadOnlyList<InstallationCandidate> Locate(string? commandLineDirectory)
    {
        // An explicit target is authoritative.  If it is malformed, points at
        // a protected root/profile directory, or is not a verified pet install,
        // do not silently substitute a different registry or legacy target.
        if (commandLineDirectory is not null)
        {
            if (!TryGetVerifiedDirectory(commandLineDirectory, out var normalizedCommandLineDirectory))
            {
                return [];
            }

            var innoCandidates = ReadVerifiedInnoCandidates();
            var innoCandidate = innoCandidates.FirstOrDefault(candidate =>
                candidate.InstallDirectory.Equals(normalizedCommandLineDirectory, StringComparison.OrdinalIgnoreCase));
            return [innoCandidate ?? new InstallationCandidate(normalizedCommandLineDirectory, InstallKind.Direct, null)];
        }

        var verifiedInnoCandidates = ReadVerifiedInnoCandidates();
        if (verifiedInnoCandidates.Length > 0)
        {
            return verifiedInnoCandidates;
        }

        return store.ReadLegacyDirectories()
            .Select(TryCreateVerifiedCandidate)
            .OfType<InstallationCandidate>()
            .DistinctBy(candidate => candidate.InstallDirectory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private InstallationCandidate[] ReadVerifiedInnoCandidates() =>
        store.ReadInnoCandidates()
            .Select(candidate =>
            {
                var verified = TryCreateVerifiedCandidate(candidate.InstallDirectory);
                return verified is null ? null : candidate with { InstallDirectory = verified.InstallDirectory };
            })
            .OfType<InstallationCandidate>()
            .DistinctBy(candidate => candidate.InstallDirectory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private InstallationCandidate? TryCreateVerifiedCandidate(string? directory)
    {
        if (!TryGetVerifiedDirectory(directory, out var normalized))
        {
            return null;
        }

        return new InstallationCandidate(normalized, InstallKind.Direct, null);
    }

    private bool TryGetVerifiedDirectory(string? directory, out string normalized)
    {
        normalized = string.Empty;
        return InstallPathPolicy.TryCreate(directory ?? string.Empty, profileDirectory, out normalized) &&
               isVerifiedInstallation(normalized);
    }
}
