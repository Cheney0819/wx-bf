namespace Footprint.Core;

public sealed record PlainExportCandidate(
    string Slot,
    int SourceIndex,
    string SourceName,
    string WorkingDirectory,
    string SourcePath,
    string TemporaryPath,
    string FinalPath,
    DatabaseManifest Database);

public sealed record PlainExportExclusion(int SourceIndex, string SourceName, string Reason, DatabaseManifest Database);
public sealed record PlainExportIneligible(
    string Slot,
    int SourceIndex,
    string SourceName,
    string Reason,
    DatabaseManifest Database);

public sealed record PlainExportPlan(
    string RootDirectory,
    IReadOnlyList<PlainExportCandidate> Exportable,
    IReadOnlyList<PlainExportExclusion> Excluded,
    IReadOnlyList<PlainExportIneligible> Ineligible)
{
    public int Expected => Exportable.Count + Ineligible.Count;
}

public static class PlainDbPlanner
{
    private const int WindowsPathBudget = 220;
    private static readonly HashSet<char> WindowsInvalidFileNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static PlainExportPlan Create(string sessionDirectory, SessionManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        var root = Path.Combine(sessionDirectory, "plain-db");
        var exclusions = new List<PlainExportExclusion>();
        var business = new List<(int Index, string Name, DatabaseManifest Database)>();

        for (var index = 0; index < manifest.Databases.Count; index++)
        {
            var database = manifest.Databases[index];
            var name = string.IsNullOrWhiteSpace(database.Path) ? string.Empty : SourceFileName(database.Path);
            if (string.Equals(name, "weclaw.db", StringComparison.OrdinalIgnoreCase))
            {
                exclusions.Add(new PlainExportExclusion(index + 1, name, "excluded-by-policy", database));
                continue;
            }

            business.Add((index + 1, name, database));
        }

        var ordered = business.OrderBy(item => NormalizeSourcePath(item.Database.Path),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Index)
            .ToArray();
        var exportable = new List<PlainExportCandidate>(ordered.Length);
        var ineligible = new List<PlainExportIneligible>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var slot = $"d{index + 1:D2}";
            var directory = Path.Combine(root, slot);
            var workingDirectory = Path.Combine(directory, ".source");
            var final = Path.Combine(directory, item.Name);
            var temporary = final + ".tmp";
            var failure = CandidateFailure(item.Name, temporary, Path.Combine(workingDirectory, item.Name), item.Database);
            if (failure is not null)
            {
                ineligible.Add(new PlainExportIneligible(slot, item.Index, item.Name, failure, item.Database));
                continue;
            }
            exportable.Add(new PlainExportCandidate(slot, item.Index, item.Name, workingDirectory,
                Path.Combine(workingDirectory, item.Name), temporary, final, item.Database));
        }

        return new PlainExportPlan(root, exportable, exclusions, ineligible);
    }

    public static string SourceFileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string NormalizeSourcePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').Trim().TrimEnd('/');

    private static string? CandidateFailure(string name, string temporary, string workingSource,
        DatabaseManifest database)
    {
        if (string.IsNullOrWhiteSpace(database.Path)) return "source-path-missing";
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Any(character => character < 32 || WindowsInvalidFileNameChars.Contains(character)))
            return "unsafe-database-filename";
        if (!database.Snapshot.Stable || !database.Verification.Accepted) return "source-not-eligible";
        if (string.IsNullOrWhiteSpace(database.Snapshot.Directory)) return "snapshot-directory-missing";
        if (new[] { temporary, temporary + ".counts.sql", workingSource }.Any(path =>
                Path.GetFullPath(path).Length > WindowsPathBudget)) return "output-path-too-long";
        return null;
    }
}
