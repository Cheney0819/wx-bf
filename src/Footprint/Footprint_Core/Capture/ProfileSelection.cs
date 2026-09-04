using System.Security.Cryptography;

namespace Footprint.Core.Capture;

public sealed record ProfileSelectionResult(bool Accepted, TargetProfile? Profile, string DllSha256,
    string ErrorCode, string MessageZh, bool MayControlProcess, IReadOnlyList<string> EvidenceCodes);

public sealed record ProfileCatalogSelection(ProfileSelectionResult Selection, string? ProfilePath)
{
    public TargetProfile? Profile => Selection.Profile;
}

public sealed class ProfileCatalog(Func<string, TargetProfile>? loader = null)
{
    private readonly Func<string, TargetProfile> _loader = loader ?? TargetProfile.Load;

    public ProfileCatalogSelection Select(string dllPath, IReadOnlyList<string> profilePaths)
    {
        ArgumentNullException.ThrowIfNull(profilePaths);
        var entries = profilePaths
            .Select(path => (Path: path, Profile: _loader(path)))
            .ToArray();
        var selection = new ProfileSelection().Select(dllPath,
            entries.Select(item => item.Profile).ToArray());
        var selectedPath = selection.Accepted
            ? entries.Single(item => ReferenceEquals(item.Profile, selection.Profile)).Path
            : null;
        return new ProfileCatalogSelection(selection, selectedPath);
    }
}

public sealed class ProfileSelection
{
    private const string UnsupportedCode = "weixin_profile_unsupported";
    private const string UnsupportedMessage = "当前微信版本不受支持，已停止采集且不会控制微信进程。";
    private readonly Action? _afterSnapshotHash;

    public ProfileSelection() { }

    internal ProfileSelection(Action? afterSnapshotHash) => _afterSnapshotHash = afterSnapshotHash;

    public ProfileSelectionResult Select(string dllPath, IReadOnlyList<TargetProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || profiles is null)
            return Unsupported(string.Empty, "input_invalid");

        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 64, FileOptions.RandomAccess);
            var dllSha256 = HashSnapshot(stream);
            var duplicateIds = profiles
                .Where(profile => profile is not null && !string.IsNullOrEmpty(profile.ProfileId))
                .GroupBy(profile => profile.ProfileId, StringComparer.Ordinal)
                .Any(group => group.Count() > 1);
            if (duplicateIds) return Unsupported(dllSha256, "profile_id_duplicate");

            var duplicateShas = profiles
                .Where(profile => profile is not null && IsSha256(profile.DllSha256))
                .GroupBy(profile => profile.DllSha256, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1);
            if (duplicateShas) return Unsupported(dllSha256, "profile_sha_duplicate");

            var matching = profiles
                .Where(profile => profile is not null && string.Equals(profile.DllSha256, dllSha256, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length != 1) return Unsupported(dllSha256, "profile_sha_unmatched");

            var profile = matching[0];
            var validation = profile.Validate();
            if (!validation.IsValid)
                return Unsupported(dllSha256, validation.Errors.Select(error => $"profile_invalid:{error}").ToArray());
            if (!string.Equals(Path.GetFileName(dllPath), profile.ModuleName, StringComparison.OrdinalIgnoreCase))
                return Unsupported(dllSha256, "module_name_path_mismatch");

            _afterSnapshotHash?.Invoke();
            stream.Position = 0;
            var peVerification = PeVerifier.Verify(stream, profile);
            if (!peVerification.IsValid) return Unsupported(dllSha256, peVerification.Errors);

            return new ProfileSelectionResult(true, profile, dllSha256, string.Empty, string.Empty, true, []);
        }
        catch (Exception)
        {
            return Unsupported(string.Empty, "dll_snapshot_unavailable");
        }
    }

    private static string HashSnapshot(Stream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static ProfileSelectionResult Unsupported(string dllSha256, params string[] evidenceCodes) =>
        new(false, null, dllSha256, UnsupportedCode, UnsupportedMessage, false, evidenceCodes);

    private static ProfileSelectionResult Unsupported(string dllSha256, IReadOnlyList<string> evidenceCodes) =>
        new(false, null, dllSha256, UnsupportedCode, UnsupportedMessage, false, evidenceCodes);
}
