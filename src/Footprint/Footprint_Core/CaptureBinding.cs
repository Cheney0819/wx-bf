namespace Footprint.Core;

public sealed record CaptureEvent(
    string Kind,
    string Boundary,
    int ThreadId,
    string? Wrapper,
    string? Core,
    string? Path,
    int? Tag,
    int? PageSize,
    int? Compatibility,
    string? DbPointer,
    string? KeySha256,
    int? KeyLength,
    long TimestampMilliseconds,
    string? StackFingerprint = null,
    string? BusinessKeySha256 = null,
    string? PathFromDb = null,
    string? ProtectedKeyPath = null,
    string? ProfileSha256 = null)
{
    public static CaptureEvent Profile(int threadId, string wrapper, string core, string path, int tag,
        int pageSize, int compatibility, long timestampMilliseconds, string? businessKeySha256 = null,
        string? profileSha256 = null) =>
        new("profile", "config_cipher", threadId, wrapper, core, path, tag, pageSize, compatibility,
            null, null, null, timestampMilliseconds, null, businessKeySha256, ProfileSha256: profileSha256);

    public static CaptureEvent Key(string boundary, int threadId, string dbPointer, string keySha256,
        int keyLength, long timestampMilliseconds, string? stackFingerprint = null, string? pathFromDb = null,
        string? protectedKeyPath = null, string? profileSha256 = null) =>
        new("key", boundary, threadId, null, null, null, null, null, null, dbPointer, keySha256,
            keyLength, timestampMilliseconds, stackFingerprint, null, pathFromDb, protectedKeyPath, profileSha256);
}

public sealed record DatabaseBinding(
    string Path,
    int Tag,
    string Wrapper,
    string Core,
    string DbPointer,
    string KeySha256,
    int KeyLength,
    int PageSize,
    int Compatibility,
    IReadOnlyList<CaptureEvent> Evidence,
    string? PathFromDb = null,
    string? ProfileSha256 = null);

public sealed record CaptureAmbiguity(string Reason, IReadOnlyList<CaptureEvent> Events);
public sealed record CaptureBuildResult(IReadOnlyList<DatabaseBinding> Bindings, IReadOnlyList<CaptureAmbiguity> Ambiguities);

public sealed class CaptureBinder
{
    private static readonly string[] RequiredBoundaries = ["wcdb_apply_key", "sqlite3CodecAttach"];
    private static readonly string[] SqliteKeyBoundaries = ["sqlite3_key", "sqlite3_key_v2"];
    private readonly List<CaptureEvent> _events = [];
    private readonly long _windowMs;
    private readonly Func<string, bool> _pathExists;

    public CaptureBinder(TimeSpan correlationWindow, Func<string, bool>? pathExists = null)
    {
        _windowMs = checked((long)correlationWindow.TotalMilliseconds);
        _pathExists = pathExists ?? (_ => true);
    }

    public void Add(CaptureEvent item) => _events.Add(item);

    public CaptureBuildResult Build()
    {
        var bindingCandidates = new List<BindingCandidate>();
        var ambiguities = new List<CaptureAmbiguity>();
        var invalidProfileEvidence = _events.Where(IsRequiredProfileEvidence)
            .Where(item => !IsSha256(item.ProfileSha256)).ToArray();
        if (invalidProfileEvidence.Length > 0)
            return new CaptureBuildResult([],
                [new CaptureAmbiguity("必需边界证据缺少有效的配置 SHA-256，已停止绑定。", invalidProfileEvidence)]);

        var profiles = _events.Where(IsCompleteProfile).ToArray();
        var keys = _events.Where(IsKeyEvidence).ToArray();

        if (keys.Length > 0 && profiles.Length == 0)
            ambiguities.Add(new CaptureAmbiguity("缺少数据库配置路径与标签证据，已停止绑定。", keys));

        var keyGroups = keys.GroupBy(item =>
            {
                var exactPath = string.IsNullOrWhiteSpace(item.PathFromDb)
                    ? null
                    : NormalizePath(item.PathFromDb).ToUpperInvariant();
                return new KeyGroupIdentity(exactPath is null ? item.DbPointer : null,
                    item.KeySha256, item.KeyLength, exactPath);
            })
            .Select(CreateKeyGroup).ToArray();
        foreach (var keyGroup in keyGroups)
        {
            var group = keyGroup.Events;
            if (!keyGroup.HasRequiredBoundaries)
            {
                ambiguities.Add(new CaptureAmbiguity(
                    "数据库密钥证据缺少必需的应用密钥、SQLite 设置密钥或编码附加边界，已停止绑定。", group));
                continue;
            }

            var profileShas = group.Select(item => item.ProfileSha256).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (profileShas.Length != 1 || profiles.Any(profile => !string.Equals(profile.ProfileSha256,
                    profileShas[0], StringComparison.OrdinalIgnoreCase)))
            {
                ambiguities.Add(new CaptureAmbiguity("三边界证据的配置 SHA-256 不一致，已停止绑定。", group));
                continue;
            }

            if (!keyGroup.HasUniqueProtectedKey)
            {
                ambiguities.Add(new CaptureAmbiguity("缺少唯一且有效的受保护密钥落盘证据，已停止绑定。", group));
                continue;
            }

            var exactPaths = group.Select(item => item.PathFromDb).OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(PathComparer.Instance).ToArray();
            if (exactPaths.Length > 1)
            {
                ambiguities.Add(new CaptureAmbiguity("同一数据库指针对应多个精确路径，已停止绑定。", group));
                continue;
            }

            var threads = group.Select(item => item.ThreadId).Distinct().ToArray();
            if (threads.Length != 1 && exactPaths.Length != 1)
            {
                ambiguities.Add(new CaptureAmbiguity("跨线程密钥边界缺少同一精确数据库路径，已停止绑定。", group));
                continue;
            }

            var pathFromDb = exactPaths.SingleOrDefault();
            CaptureEvent[] candidates;
            if (pathFromDb is not null)
            {
                candidates = profiles.Where(profile => PathsEqual(profile.Path, pathFromDb)).ToArray();
            }
            else
            {
                var minTime = group.Min(item => item.TimestampMilliseconds) - _windowMs;
                var maxTime = group.Max(item => item.TimestampMilliseconds) + _windowMs;
                var thread = threads.Single();
                candidates = profiles.Where(profile => profile.ThreadId == thread &&
                    profile.TimestampMilliseconds >= minTime && profile.TimestampMilliseconds <= maxTime).ToArray();
            }

            candidates = candidates.DistinctBy(item => (PathComparisonKey(item.Path!), item.Tag)).ToArray();
            var direct = candidates.Where(profile => string.Equals(profile.BusinessKeySha256,
                keyGroup.KeySha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (direct.Length > 0) candidates = direct;
            candidates = SelectByStackFingerprint(candidates, group);

            if (candidates.Length != 1)
            {
                if (candidates.Length == 0) continue;
                ambiguities.Add(new CaptureAmbiguity(candidates.Length == 0
                    ? "没有唯一的路径与标签证据匹配三边界，已停止绑定。"
                    : "同一数据库存在多个可能的路径或标签，已停止绑定。",
                    group.Concat(candidates).ToArray()));
                continue;
            }

            var profile = candidates[0];
            if (!_pathExists(profile.Path!))
            {
                ambiguities.Add(new CaptureAmbiguity("唯一匹配的数据库路径不存在，已停止绑定。", group.Prepend(profile).ToArray()));
                continue;
            }

            bindingCandidates.Add(new BindingCandidate(
                new DatabaseBinding(profile.Path!, profile.Tag!.Value, profile.Wrapper!, profile.Core!,
                    keyGroup.DbPointer!, keyGroup.KeySha256!, keyGroup.KeyLength ?? 0,
                    profile.PageSize ?? 4096, profile.Compatibility ?? 4,
                    group.Prepend(profile).Concat(keyGroup.MatchingKeyspecs)
                        .Append(keyGroup.ProtectedKeyEvidence!).Distinct().ToArray(),
                    pathFromDb is null ? null : profile.Path, profileShas[0]),
                CountCompleteSequences(group), keyGroup.MatchingKeyspecs.Length > 0,
                keyGroup.HasDirectProtectedEvidence,
                group.Max(item => item.TimestampMilliseconds)));
        }

        var rejected = new HashSet<BindingCandidate>();
        foreach (var group in bindingCandidates.GroupBy(candidate => NormalizePath(candidate.Binding.Path),
                     StringComparer.OrdinalIgnoreCase))
        {
            var active = group.Where(candidate => !rejected.Contains(candidate)).ToArray();
            var keyIdentities = active.GroupBy(candidate =>
                (candidate.Binding.KeySha256.ToUpperInvariant(), candidate.Binding.KeyLength)).ToArray();
            if (keyIdentities.Length > 1)
            {
                var bestPreference = keyIdentities.Max(identity => KeyPreference(group.Key, identity.Key.KeyLength));
                var strongest = keyIdentities.Where(identity =>
                    KeyPreference(group.Key, identity.Key.KeyLength) == bestPreference).ToArray();
                if (strongest.Length > 1)
                {
                    var withKeyspec = strongest.Where(identity => identity.Any(candidate =>
                        candidate.HasMatchingKeyspec)).ToArray();
                    if (withKeyspec.Length == 1) strongest = withKeyspec;
                    else
                    {
                        rejected.UnionWith(active);
                        ambiguities.Add(new CaptureAmbiguity("同一路径对应多个完整数据库密钥序列，已停止绑定。",
                            active.SelectMany(candidate => candidate.Binding.Evidence).ToArray()));
                        continue;
                    }
                }

                var selectedIdentity = strongest[0].Key;
                rejected.UnionWith(active.Where(candidate =>
                    (candidate.Binding.KeySha256.ToUpperInvariant(), candidate.Binding.KeyLength) !=
                    selectedIdentity));
                active = active.Where(candidate =>
                    (candidate.Binding.KeySha256.ToUpperInvariant(), candidate.Binding.KeyLength) ==
                    selectedIdentity).ToArray();
            }

            foreach (var identityGroup in active.GroupBy(candidate =>
                         (candidate.Binding.KeySha256.ToUpperInvariant(), candidate.Binding.KeyLength,
                             candidate.Binding.Tag)))
            {
                var duplicates = identityGroup.ToArray();
                var exact = duplicates.Where(candidate => candidate.Binding.PathFromDb is not null).ToArray();
                if (exact.Length > 0)
                {
                    var selected = exact.OrderByDescending(candidate => candidate.HasDirectProtectedEvidence)
                        .ThenByDescending(candidate => candidate.HasMatchingKeyspec)
                        .ThenByDescending(candidate => candidate.LatestTimestampMilliseconds).First();
                    rejected.UnionWith(duplicates.Where(candidate => candidate != selected));
                    continue;
                }

                if (duplicates.Length == 1)
                {
                    var candidate = duplicates[0];
                    if (candidate.CompleteSequenceCount <= 1 || candidate.HasMatchingKeyspec) continue;
                    rejected.Add(candidate);
                    ambiguities.Add(new CaptureAmbiguity("同一路径与标签存在多个完整数据库序列，已停止绑定。",
                        candidate.Binding.Evidence));
                    continue;
                }

                rejected.UnionWith(duplicates);
                ambiguities.Add(new CaptureAmbiguity("同一路径与标签存在多个完整数据库序列，已停止绑定。",
                    duplicates.SelectMany(candidate => candidate.Binding.Evidence).ToArray()));
            }
        }

        var promotedBindings = new List<DatabaseBinding>();
        foreach (var candidate in bindingCandidates.Where(candidate => !rejected.Contains(candidate)))
        {
            var promotion = PromoteExactPathCodecKeyspec(candidate.Binding);
            if (promotion.Ambiguity is not null) ambiguities.Add(promotion.Ambiguity);
            else promotedBindings.Add(promotion.Binding);
        }
        return new CaptureBuildResult(promotedBindings,
            ambiguities.DistinctBy(item => (item.Reason, string.Join('|', item.Events.Select(EventIdentity)))).ToArray());
    }

    private static bool IsRequiredProfileEvidence(CaptureEvent item) =>
        string.Equals(item.Boundary, "config_cipher", StringComparison.Ordinal) ||
        string.Equals(item.Boundary, "path_getter", StringComparison.Ordinal) ||
        string.Equals(item.Boundary, "wcdb_apply_key", StringComparison.Ordinal) ||
        string.Equals(item.Boundary, "sqlite3CodecAttach", StringComparison.Ordinal) ||
        SqliteKeyBoundaries.Contains(item.Boundary, StringComparer.Ordinal);

    private static bool IsCompleteProfile(CaptureEvent item) =>
        string.Equals(item.Kind, "profile", StringComparison.Ordinal) &&
        (string.Equals(item.Boundary, "config_cipher", StringComparison.Ordinal) ||
         string.Equals(item.Boundary, "path_getter", StringComparison.Ordinal)) &&
        !string.IsNullOrWhiteSpace(item.Path) && !string.IsNullOrWhiteSpace(item.Wrapper) &&
        !string.IsNullOrWhiteSpace(item.Core) && item.Tag is not null;

    private static bool IsKeyEvidence(CaptureEvent item) => string.Equals(item.Kind, "key", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(item.DbPointer) && !string.IsNullOrWhiteSpace(item.KeySha256);

    private static bool HasRequiredBoundaries(IEnumerable<CaptureEvent> events)
    {
        var boundaries = events.Select(item => item.Boundary).ToHashSet(StringComparer.Ordinal);
        return RequiredBoundaries.All(boundaries.Contains) && SqliteKeyBoundaries.Any(boundaries.Contains);
    }

    private static int CountCompleteSequences(IEnumerable<CaptureEvent> events)
    {
        var evidence = events.ToArray();
        var applyCount = evidence.Count(item =>
            string.Equals(item.Boundary, "wcdb_apply_key", StringComparison.Ordinal));
        var sqliteCount = evidence.Count(item =>
            SqliteKeyBoundaries.Contains(item.Boundary, StringComparer.Ordinal));
        var attachCount = evidence.Count(item =>
            string.Equals(item.Boundary, "sqlite3CodecAttach", StringComparison.Ordinal));
        return Math.Min(applyCount, Math.Min(sqliteCount, attachCount));
    }

    private KeyGroup CreateKeyGroup(IGrouping<KeyGroupIdentity, CaptureEvent> source)
    {
        var events = source.OrderBy(item => item.TimestampMilliseconds).ToArray();
        var exactPaths = events.Select(item => item.PathFromDb).OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(PathComparer.Instance).ToArray();
        var profileShas = events.Select(item => item.ProfileSha256).OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matchingKeyspecs = _events.Where(item =>
                string.Equals(item.Kind, "codec_keyspec", StringComparison.Ordinal) &&
                string.Equals(item.Boundary, "sqlite3CodecGetKey", StringComparison.Ordinal) &&
                string.Equals(item.KeySha256, source.Key.KeySha256, StringComparison.OrdinalIgnoreCase) &&
                item.KeyLength == source.Key.KeyLength && PathsEqual(item.PathFromDb, exactPaths.SingleOrDefault()) &&
                profileShas.Length == 1 && string.Equals(item.ProfileSha256, profileShas[0],
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.TimestampMilliseconds).ToArray();
        var protectedPaths = _events.Where(item =>
                string.Equals(item.KeySha256, source.Key.KeySha256, StringComparison.OrdinalIgnoreCase) &&
                item.KeyLength == source.Key.KeyLength && profileShas.Length == 1 &&
                string.Equals(item.ProfileSha256, profileShas[0], StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ProtectedKeyPath).OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(PathComparer.Instance).ToArray();
        var directProtectedPaths = events.Select(item => item.ProtectedKeyPath).OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(PathComparer.Instance).ToArray();
        var protectedKeyEvidence = protectedPaths.Length == 1 && _pathExists(protectedPaths[0])
            ? _events.Where(item => string.Equals(item.KeySha256, source.Key.KeySha256,
                    StringComparison.OrdinalIgnoreCase) && item.KeyLength == source.Key.KeyLength &&
                string.Equals(item.ProfileSha256, profileShas.SingleOrDefault(), StringComparison.OrdinalIgnoreCase) &&
                PathsEqual(item.ProtectedKeyPath, protectedPaths[0]))
                .OrderByDescending(item => item.TimestampMilliseconds).FirstOrDefault()
            : null;
        return new KeyGroup(SelectRepresentativeDbPointer(events), source.Key.KeySha256, source.Key.KeyLength, events,
            HasRequiredBoundaries(events), exactPaths.Length == 1 ? exactPaths[0] : null,
            profileShas.Length == 1 ? profileShas[0] : null,
            protectedKeyEvidence is not null, protectedKeyEvidence,
            directProtectedPaths.Length == 1 && _pathExists(directProtectedPaths[0]), matchingKeyspecs);
    }

    private static string? SelectRepresentativeDbPointer(CaptureEvent[] events) => events
        .Where(item => !string.IsNullOrWhiteSpace(item.DbPointer))
        .GroupBy(item => item.DbPointer!, StringComparer.Ordinal)
        .Select(group => new
        {
            Pointer = group.Key,
            HasProtectedKeyEvidence = group.Any(item => !string.IsNullOrWhiteSpace(item.ProtectedKeyPath)),
            CompleteSequences = CountCompleteSequences(group),
            LatestTimestamp = group.Max(item => item.TimestampMilliseconds)
        })
        .OrderByDescending(item => item.HasProtectedKeyEvidence)
        .ThenByDescending(item => item.CompleteSequences)
        .ThenByDescending(item => item.LatestTimestamp)
        .Select(item => item.Pointer)
        .FirstOrDefault();

    private KeyspecPromotion PromoteExactPathCodecKeyspec(DatabaseBinding binding)
    {
        var samePath = _events.Where(item =>
                string.Equals(item.Kind, "codec_keyspec", StringComparison.Ordinal) &&
                string.Equals(item.Boundary, "sqlite3CodecGetKey", StringComparison.Ordinal) &&
                PathsEqual(item.PathFromDb, binding.Path) &&
                (!IsHardlinkDatabase(binding.Path) || item.KeyLength == 32))
            .OrderByDescending(item => KeyPreference(binding.Path, item.KeyLength))
            .ThenByDescending(item => item.TimestampMilliseconds).ToArray();
        if (samePath.Length == 0) return new KeyspecPromotion(binding, null);

        var sameKey = samePath.Where(item =>
            string.Equals(item.KeySha256, binding.KeySha256, StringComparison.OrdinalIgnoreCase) &&
            KeyPreference(binding.Path, item.KeyLength) >= KeyPreference(binding.Path, binding.KeyLength)).ToArray();
        if (sameKey.Length == 0) return new KeyspecPromotion(binding, null);

        var invalid = sameKey.Where(item => !IsSha256(item.ProfileSha256) ||
            !string.Equals(item.ProfileSha256, binding.ProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(item.ProtectedKeyPath) && !_pathExists(item.ProtectedKeyPath))).ToArray();
        if (invalid.Length > 0)
            return new KeyspecPromotion(binding, new CaptureAmbiguity(
                "同一路径存在来源不一致或无效的密钥规格证据，已停止绑定。",
                binding.Evidence.Concat(samePath).ToArray()));

        var keyspec = sameKey.OrderByDescending(item => KeyPreference(binding.Path, item.KeyLength))
            .ThenByDescending(item => item.TimestampMilliseconds).First();
        return new KeyspecPromotion(binding with
        {
            KeyLength = keyspec.KeyLength ?? binding.KeyLength,
            Evidence = binding.Evidence.Append(keyspec).ToArray(),
            PathFromDb = binding.Path
        }, null);
    }

    private static CaptureEvent[] SelectByStackFingerprint(CaptureEvent[] profiles, CaptureEvent[] keyEvents)
    {
        var fingerprints = keyEvents.Select(item => item.StackFingerprint).Where(value => value is not null)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (fingerprints.Length != 1) return profiles;
        var matching = profiles.Where(profile => string.Equals(profile.StackFingerprint, fingerprints[0],
            StringComparison.Ordinal)).ToArray();
        return matching.Length > 0 ? matching : profiles;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var a = NormalizePath(left);
        var b = NormalizePath(right);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        const string marker = @"\db_storage\";
        var aIndex = a.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var bIndex = b.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (aIndex >= 0 && bIndex >= 0)
            return string.Equals(a[aIndex..], b[bIndex..], StringComparison.OrdinalIgnoreCase);
        return a.EndsWith(@"\" + b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(@"\" + a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value) => value.Trim().Replace('/', '\\').TrimEnd('\\');
    private static string PathComparisonKey(string value)
    {
        var normalized = NormalizePath(value);
        const string marker = @"\db_storage\";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? normalized[index..].ToUpperInvariant() : normalized.ToUpperInvariant();
    }
    private static string EventIdentity(CaptureEvent item) => $"{item.Boundary}:{item.DbPointer}:{item.TimestampMilliseconds}";
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static int KeyPreference(string path, int? length) => IsHardlinkDatabase(path)
        ? length switch { 32 => 3, 99 => 2, 67 => 1, _ => 0 }
        : length switch { 99 => 3, 67 => 2, 32 => 1, _ => 0 };

    private static bool IsHardlinkDatabase(string path) => NormalizePath(path)
        .EndsWith(@"\hardlink\hardlink.db", StringComparison.OrdinalIgnoreCase);

    private sealed record KeyspecPromotion(DatabaseBinding Binding, CaptureAmbiguity? Ambiguity);
    private sealed record KeyGroupIdentity(string? DbPointer, string? KeySha256, int? KeyLength, string? ExactPath);
    private sealed record KeyGroup(string? DbPointer, string? KeySha256, int? KeyLength, CaptureEvent[] Events,
        bool HasRequiredBoundaries, string? ExactPath, string? ProfileSha256, bool HasUniqueProtectedKey,
        CaptureEvent? ProtectedKeyEvidence, bool HasDirectProtectedEvidence, CaptureEvent[] MatchingKeyspecs);
    private sealed record BindingCandidate(DatabaseBinding Binding, int CompleteSequenceCount,
        bool HasMatchingKeyspec, bool HasDirectProtectedEvidence, long LatestTimestampMilliseconds);

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static PathComparer Instance { get; } = new();
        public bool Equals(string? x, string? y) => PathsEqual(x, y);
        public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(PathComparisonKey(obj));
    }
}
