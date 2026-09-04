using System.Text.Json;

namespace Footprint.Core;

public sealed class ChatImageRecoveryService
{
    private readonly ChatImageIndexReader _indexReader;
    private readonly ChatImageFileLocator _locator;
    private readonly ChatImageMediaSnapshotter _mediaSnapshotter;
    private readonly IChatImageProtocolProvider _protocolProvider;
    private readonly IChatImageDecryptor _decryptor;
    private readonly IChatImagePayloadNormalizer _payloadNormalizer;
    private static readonly JsonSerializerOptions JsonLineOptions = new(TargetProfile.JsonOptions)
    { WriteIndented = false };

    public ChatImageRecoveryService(ChatImageIndexReader? indexReader = null, ChatImageFileLocator? locator = null,
        ChatImageMediaSnapshotter? mediaSnapshotter = null,
        IChatImageProtocolProvider? protocolProvider = null, IChatImageDecryptor? decryptor = null,
        IChatImagePayloadNormalizer? payloadNormalizer = null)
    {
        _indexReader = indexReader ?? new ChatImageIndexReader();
        _locator = locator ?? new ChatImageFileLocator();
        _mediaSnapshotter = mediaSnapshotter ?? new ChatImageMediaSnapshotter();
        _protocolProvider = protocolProvider ?? new UnverifiedChatImageProtocolProvider();
        _decryptor = decryptor ?? new ChatImageDecryptor();
        _payloadNormalizer = payloadNormalizer ?? new ChatImagePayloadNormalizer();
    }

    public async Task<ChatImageRecoveryResult> RecoverAsync(string sessionDirectory, SessionManifest session,
        string sqliteExecutable, CancellationToken cancellationToken,
        Action<string, double>? progress = null)
    {
        var root = Path.Combine(sessionDirectory, "chat-images");
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        var failuresPath = Path.Combine(root, "failures.jsonl");
        var manifest = new ChatImageManifest();
        var hardlink = session.Databases.FirstOrDefault(database =>
            string.Equals(PlainDbPlanner.SourceFileName(database.Path), "hardlink.db", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(database.PlaintextExport.Status, "passed", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(database.PlaintextExport.OutputPath));
        if (hardlink is null)
            return await CompleteAsync(manifest, manifestPath, failuresPath, cancellationToken);

        var relativePlain = hardlink.PlaintextExport.OutputPath!.Replace('/', Path.DirectorySeparatorChar);
        var plainPath = Path.Combine(sessionDirectory, relativePlain);
        manifest.IndexDatabase = hardlink.PlaintextExport.OutputPath;
        if (!ChatImageAccountRoot.TryResolve(hardlink.Path, out var accountRoot) || accountRoot is null)
        {
            manifest.Status = "failed";
            manifest.Items.Add(Failure(new ChatImageIndexRecord(), "account_root_unresolved",
                "The verified hardlink.db path does not match the required account layout."));
            manifest.Failed = 1;
            return await CompleteAsync(manifest, manifestPath, failuresPath, cancellationToken);
        }
        manifest.AccountRoot = RedactAccountRoot(accountRoot);

        var resourceEvidence = await ReadResourceEvidenceAsync(sessionDirectory, session, sqliteExecutable,
            cancellationToken);

        progress?.Invoke("读取 hardlink.db 图片索引", 0.1);
        ChatImageIndexReadResult index;
        try { index = await _indexReader.ReadAsync(plainPath, sqliteExecutable, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            manifest.Status = "failed";
            manifest.Items.Add(Failure(new ChatImageIndexRecord(), "index_row_invalid",
                "The verified hardlink.db image index could not be read."));
            manifest.Failed = 1;
            return await CompleteAsync(manifest, manifestPath, failuresPath, cancellationToken);
        }
        manifest.Expected = index.Expected + index.Errors.Count;
        var runtimeProbe = ChatImageRuntimePathProbeReader.Read(Path.Combine(sessionDirectory, "capture"), index.Items);
        manifest.RuntimePathProbe = new ChatImageRuntimePathProbeSummary
        {
            EventCount = runtimeProbe.EventCount,
            MediaEventCount = runtimeProbe.MediaEventCount,
            MatchedIndexCount = runtimeProbe.MatchedIndexCount,
            SuccessfulOpenCount = runtimeProbe.SuccessfulOpenCount,
            RootCounts = new Dictionary<string, int>(runtimeProbe.RootCounts, StringComparer.OrdinalIgnoreCase)
        };
        if (index.Errors.Count > 0)
        {
            foreach (var item in index.Items)
                manifest.Items.Add(Failure(item, "index_row_invalid",
                    "The image index contains invalid rows; publication was stopped."));
            for (var position = 0; position < index.Errors.Count; position++)
                manifest.Items.Add(Failure(new ChatImageIndexRecord(), "index_row_invalid",
                    "The image index contains an invalid row."));
            manifest.Failed = manifest.Expected;
            manifest.Status = "failed";
            return await CompleteAsync(manifest, manifestPath, failuresPath, cancellationToken);
        }
        progress?.Invoke("复制索引中的本地图片文件", 0.2);
        var mediaSnapshot = await _mediaSnapshotter.CreateAsync(hardlink.Path, sessionDirectory, index.Items,
            cancellationToken);
        manifest.MediaSnapshot = new ChatImageMediaSnapshotSummary
        {
            Directory = mediaSnapshot.Directory is null ? null : Path.GetRelativePath(sessionDirectory,
                mediaSnapshot.Directory).Replace('\\', '/'),
            CopiedFileCount = mediaSnapshot.CopiedFileCount,
            MatchedIndexCount = mediaSnapshot.MatchedIndexCount,
            MissingIndexCount = mediaSnapshot.MissingIndexCount,
            Errors = mediaSnapshot.Errors.ToList()
        };
        progress?.Invoke("建立账号图片文件索引", 0.25);
        var snapshotDirectory = mediaSnapshot.Directory ?? Path.Combine(root, "media-snapshot");
        var locations = _locator.Locate(hardlink.Path, index.Items, cancellationToken, snapshotDirectory);
        var protocol = _protocolProvider.GetEvidence(session.DllSha256);
        var publisher = new ChatImagePublisher();
        var work = Path.Combine(root, ".work");
        try
        {
            for (var position = 0; position < locations.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = locations[position];
                progress?.Invoke("验证并发布本地聊天图片", 0.3 + 0.65 * (position + 1) / Math.Max(1, locations.Count));
                var enrichedIndex = ApplyResourceEvidence(location.Index, resourceEvidence, location.Candidates);
                location = location with { Index = enrichedIndex };
                if (location.ErrorCode is not null && location.ErrorCode != "ambiguous_candidates")
                {
                    manifest.Items.Add(Failure(location.Index, location.ErrorCode,
                        location.ErrorCode == "local_file_missing" ? "Indexed image is not present locally." :
                        "Multiple local files share the indexed image name."));
                    continue;
                }
                if (!protocol.IsVerifiedFor(session.DllSha256))
                {
                    manifest.Items.Add(Failure(location.Index, "protocol_not_verified",
                        "No runtime-verified offline image protocol is bound to this Weixin.dll."));
                    continue;
                }

                Directory.CreateDirectory(work);
                var valid = new List<(string Source, string Temporary, ChatImageVerification Verification,
                    SourceFingerprint Fingerprint, ChatImageCandidateDiagnostic Diagnostic)>();
                var candidateDiagnostics = new List<ChatImageCandidateDiagnostic>();
                var candidateFailures = new List<CandidateFailureContext>();
                try
                {
                    foreach (var source in location.Candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var temporary = Path.Combine(work, Guid.NewGuid().ToString("N") + ".partial");
                        var normalized = temporary;
                        var diagnostic = new ChatImageCandidateDiagnostic();
                        SourceFingerprint? before = null;
                        ChatImageVerification? verification = null;
                        string? failureCode = null;
                        string? failureSummary = null;
                        var keepNormalized = false;
                        var stage = "source_fingerprint";
                        try
                        {
                            before = await SourceFingerprint.CreateAsync(source, cancellationToken);
                            diagnostic.SourceSize = before.Size;
                            diagnostic.SourceSha256 = before.Sha256;
                            stage = "decrypt";
                            await _decryptor.DecryptAsync(source, temporary, protocol, session.DllSha256, cancellationToken);
                            stage = "decrypted_diagnostics";
                            var decrypted = await PayloadFingerprint.CreateAsync(temporary, cancellationToken);
                            diagnostic.DecryptedSize = decrypted.Size;
                            diagnostic.DecryptedSha256 = decrypted.Sha256;
                            diagnostic.DecryptedPrefixHex = decrypted.PrefixHex;
                            diagnostic.InputFormat = PayloadFormat(decrypted.Prefix);
                            if (string.Equals(diagnostic.InputFormat, "wxgf", StringComparison.Ordinal) &&
                                WxgfImageDecoder.TryParse(await File.ReadAllBytesAsync(temporary, cancellationToken),
                                    out var wxgf))
                            {
                                diagnostic.WxgfWidth = wxgf.Width;
                                diagnostic.WxgfHeight = wxgf.Height;
                                diagnostic.WxgfHevcOffset = wxgf.HevcOffset;
                            }
                            stage = "normalize";
                            normalized = await _payloadNormalizer.NormalizeAsync(temporary, work, cancellationToken);
                            stage = "normalized_diagnostics";
                            var normalizedPayload = await PayloadFingerprint.CreateAsync(normalized, cancellationToken);
                            diagnostic.NormalizedSize = normalizedPayload.Size;
                            diagnostic.NormalizedSha256 = normalizedPayload.Sha256;
                            diagnostic.NormalizedPrefixHex = normalizedPayload.PrefixHex;
                            if (string.Equals(diagnostic.InputFormat, "wxgf", StringComparison.Ordinal) &&
                                !string.Equals(normalized, temporary, StringComparison.Ordinal))
                            {
                                diagnostic.FfmpegExitCode = 0;
                                diagnostic.FfmpegTimedOut = false;
                                diagnostic.FfmpegOutputSize = normalizedPayload.Size;
                            }
                            stage = "validate";
                            verification = await ChatImageValidator.ValidateAsync(normalized, null, null, cancellationToken);
                            diagnostic.Verification = verification;
                            if (!verification.Passed)
                            {
                                failureCode = verification.ErrorCode ?? "invalid_image_payload";
                                failureSummary = verification.ErrorSummary ?? "Image verification failed.";
                                continue;
                            }
                            stage = "source_guard";
                            if (!await before.IsUnchangedAsync(source, cancellationToken))
                            {
                                failureCode = "source_changed";
                                failureSummary = "The source container changed during recovery.";
                                continue;
                            }
                            valid.Add((source, normalized, verification, before, diagnostic));
                            keepNormalized = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (ChatImagePayloadNormalizationException error)
                        {
                            diagnostic.FfmpegExitCode = error.FfmpegExitCode;
                            diagnostic.FfmpegTimedOut = error.FfmpegTimedOut;
                            diagnostic.FfmpegOutputSize = error.FfmpegOutputSize;
                            diagnostic.FfmpegErrorSummary = error.FfmpegErrorSummary;
                            failureCode = error.ErrorCode;
                            failureSummary = error.Message;
                        }
                        catch (Exception error)
                        {
                            failureCode = StageFailureCode(stage);
                            failureSummary = error.GetType().Name;
                        }
                        finally
                        {
                            var cleanupError = CleanupCandidateFiles(temporary, normalized, keepNormalized);
                            if (cleanupError is not null)
                            {
                                if (keepNormalized)
                                {
                                    valid.RemoveAll(item => string.Equals(item.Temporary, normalized,
                                        StringComparison.Ordinal));
                                    cleanupError = CombineCleanupErrors(cleanupError, DeleteFileWithRetry(normalized));
                                    keepNormalized = false;
                                }
                                failureSummary = failureCode is null
                                    ? cleanupError
                                    : $"{failureCode}; cleanup={cleanupError}";
                                failureCode = "candidate_cleanup_failed";
                            }
                            if (failureCode is not null)
                            {
                                diagnostic.ErrorCode = failureCode;
                                diagnostic.ErrorSummary = failureSummary;
                                candidateFailures.Add(new CandidateFailureContext(source, before, verification,
                                    failureCode, failureSummary ?? "Candidate processing failed."));
                            }
                            candidateDiagnostics.Add(diagnostic);
                        }
                    }

                    var groups = valid.GroupBy(item => item.Verification.OutputSha256,
                        StringComparer.OrdinalIgnoreCase).ToArray();
                    var hardlinkMd5Groups = groups.Where(group =>
                        !string.IsNullOrWhiteSpace(location.Index.Md5) && group.Any(item =>
                            string.Equals(item.Verification.OutputMd5, location.Index.Md5,
                                StringComparison.OrdinalIgnoreCase))).ToArray();
                    var selectedGroup = groups.Length == 1 ? groups[0] :
                        hardlinkMd5Groups.Length == 1 ? hardlinkMd5Groups[0] : null;
                    ChatImageManifestItem resultItem;
                    if (groups.Length == 0)
                    {
                        var selected = SelectCandidateFailure(candidateFailures);
                        resultItem = selected is null
                            ? Failure(location.Index, "candidate_processing_failed",
                                "No local candidate produced a verified image payload.", diagnostics: candidateDiagnostics)
                            : Failure(location.Index, selected.ErrorCode, selected.ErrorSummary, selected.Source,
                                accountRoot, selected.Verification, selected.Fingerprint, candidateDiagnostics);
                    }
                    else if (selectedGroup is null)
                    {
                        resultItem = Failure(location.Index, "ambiguous_candidates",
                            "Multiple local candidates produced different valid image payloads.",
                            diagnostics: candidateDiagnostics);
                    }
                    else
                    {
                        var chosen = selectedGroup.First();
                        try
                        {
                            var published = await publisher.PublishAsync(sessionDirectory, chosen.Temporary, location.Index,
                                chosen.Verification, cancellationToken);
                            resultItem = Success(location.Index, chosen.Source, accountRoot, chosen.Verification,
                                chosen.Fingerprint, published, candidateDiagnostics);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            resultItem = Failure(location.Index, "publish_failed", error.GetType().Name,
                                chosen.Source, accountRoot, chosen.Verification, chosen.Fingerprint,
                                candidateDiagnostics);
                        }
                    }

                    var cleanupFailure = CleanupValidFiles(valid);
                    valid.Clear();
                    if (cleanupFailure is not null)
                        ApplyCleanupFailure(resultItem, cleanupFailure);
                    manifest.Items.Add(resultItem);
                }
                catch (OperationCanceledException)
                {
                    CleanupValidFiles(valid);
                    throw;
                }
                catch (Exception error)
                {
                    var cleanupFailure = CleanupValidFiles(valid);
                    var selected = SelectCandidateFailure(candidateFailures);
                    var code = "candidate_processing_failed";
                    var summary = error.GetType().Name;
                    var resultItem = selected is null
                        ? Failure(location.Index, code, summary, diagnostics: candidateDiagnostics)
                        : Failure(location.Index, code, summary, selected.Source, accountRoot,
                            selected.Verification, selected.Fingerprint, candidateDiagnostics);
                    if (cleanupFailure is not null) ApplyCleanupFailure(resultItem, cleanupFailure);
                    manifest.Items.Add(resultItem);
                }
            }
        }
        finally
        {
            var cleanupFailure = DeleteDirectoryWithRetry(work);
            if (cleanupFailure is not null)
            {
                manifest.WorkCleanupStatus = "failed";
                manifest.WorkCleanupErrorCode = "work_cleanup_failed";
                manifest.WorkCleanupErrorSummary = cleanupFailure;
            }
        }

        manifest.Passed = manifest.Items.Count(item => item.Status == "passed");
        manifest.Missing = manifest.Items.Count(item => item.ErrorCode == "local_file_missing");
        manifest.Failed = manifest.Items.Count(item => item.Status == "failed" && item.ErrorCode != "local_file_missing");
        manifest.Located = manifest.Passed + manifest.Failed;
        manifest.Status = manifest.WorkCleanupErrorCode is not null ? "failed" :
            manifest.Failed > 0 ? manifest.Passed > 0 ? "partial" : "failed" :
            manifest.Passed == 0 && manifest.Missing > 0 ? "failed" :
            manifest.Expected > 0 ? "passed" : "failed";
        return await CompleteAsync(manifest, manifestPath, failuresPath, cancellationToken);
    }

    private static ChatImageManifestItem Success(ChatImageIndexRecord index, string source,
        string accountRoot, ChatImageVerification verification, SourceFingerprint fingerprint, string published,
        IReadOnlyCollection<ChatImageCandidateDiagnostic> diagnostics) => new()
        {
            IndexRowId = index.RowId,
            SourceFileName = index.FileName,
            SourceRelativePath = RedactedRelativePath(source, accountRoot),
            SourceSize = fingerprint.Size,
            SourceSha256 = fingerprint.Sha256,
            HardlinkMd5 = index.Md5,
            Dir1Id = index.Dir1Id,
            Dir1Name = StableIdentifier(index.Dir1Name ?? index.Dir1Id?.ToString()),
            Dir2Id = index.Dir2Id,
            Dir2Name = index.Dir2Name,
            ResourceId = index.ResourceId,
            ResourceMessageId = index.ResourceMessageId,
            ResourceStem = index.ResourceStem,
            ResourceType = index.ResourceType,
            ResourceSize = index.ResourceSize,
            ResourceStatus = index.ResourceStatus,
            ResourceCandidates = index.ResourceCandidates.Select(ToManifestCandidate).ToList(),
            Variant = Variant(index.Variant),
            Status = "passed",
            Format = verification.Format,
            Width = verification.Width,
            Height = verification.Height,
            OutputSize = verification.OutputSize,
            OutputMd5 = verification.OutputMd5,
            OutputSha256 = verification.OutputSha256,
            PublishedPath = published,
            Verification = verification,
            CandidateDiagnostics = diagnostics.ToList()
        };

    private static ChatImageManifestItem Failure(ChatImageIndexRecord index, string code, string summary,
        string? source = null, string? accountRoot = null, ChatImageVerification? verification = null,
        SourceFingerprint? fingerprint = null, IReadOnlyCollection<ChatImageCandidateDiagnostic>? diagnostics = null) => new()
        {
            IndexRowId = index.RowId,
            SourceFileName = index.FileName,
            SourceRelativePath = source is null || accountRoot is null ? null : RedactedRelativePath(source, accountRoot),
            SourceSize = fingerprint?.Size,
            SourceSha256 = fingerprint?.Sha256,
            HardlinkMd5 = index.Md5,
            Dir1Id = index.Dir1Id,
            Dir1Name = StableIdentifier(index.Dir1Name ?? index.Dir1Id?.ToString()),
            Dir2Id = index.Dir2Id,
            Dir2Name = index.Dir2Name,
            ResourceId = index.ResourceId,
            ResourceMessageId = index.ResourceMessageId,
            ResourceStem = index.ResourceStem,
            ResourceType = index.ResourceType,
            ResourceSize = index.ResourceSize,
            ResourceStatus = index.ResourceStatus,
            ResourceCandidates = index.ResourceCandidates.Select(ToManifestCandidate).ToList(),
            Variant = Variant(index.Variant),
            Status = code == "local_file_missing" ? "missing" : "failed",
            Verification = verification,
            CandidateDiagnostics = diagnostics?.ToList() ?? [],
            ErrorCode = code,
            ErrorSummary = summary
        };

    private static async Task<ChatImageRecoveryResult> CompleteAsync(ChatImageManifest manifest, string manifestPath,
        string failuresPath, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(manifestPath, manifest, cancellationToken);
        await AtomicFile.WriteAsync(failuresPath, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            foreach (var item in manifest.Items.Where(item => item.Status != "passed"))
            {
                token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonLineOptions));
            }
            await writer.FlushAsync(token);
        }, cancellationToken);
        return new ChatImageRecoveryResult(manifest, manifestPath, failuresPath);
    }

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, (stream, token) =>
            JsonSerializer.SerializeAsync(stream, value, TargetProfile.JsonOptions, token), cancellationToken);

    private static string RedactAccountRoot(string root) => "account-" + Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(root))[..12];
    private static string? StableIdentifier(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        "id-" + Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(value))[..12];
    private static string RedactedRelativePath(string source, string accountRoot)
    {
        var relative = Path.GetRelativePath(accountRoot, source).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Select(segment =>
            segment.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? "file-" + Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(segment))[..12] + ".dat"
                : "dir-" + Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(segment))[..12]));
    }
    private static string Variant(ChatImageVariant value) => value switch
    {
        ChatImageVariant.Full => "full",
        ChatImageVariant.Thumbnail => "thumbnail",
        _ => "unknown"
    };

    private static ChatImageResourceManifestCandidate ToManifestCandidate(ChatImageResourceRecord resource) => new()
    {
        ResourceId = resource.ResourceId,
        MessageId = resource.MessageId,
        Type = resource.Type,
        Size = resource.Size,
        Status = resource.Status
    };

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ChatImageResourceRecord>>> ReadResourceEvidenceAsync(
        string sessionDirectory, SessionManifest session, string sqliteExecutable,
        CancellationToken cancellationToken)
    {
        var database = session.Databases.FirstOrDefault(item =>
            string.Equals(PlainDbPlanner.SourceFileName(item.Path), "message_resource.db",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.PlaintextExport.Status, "passed", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.PlaintextExport.OutputPath));
        if (database is null) return new Dictionary<string, IReadOnlyList<ChatImageResourceRecord>>(StringComparer.OrdinalIgnoreCase);

        var relative = database.PlaintextExport.OutputPath!.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(sessionDirectory, relative);
        if (!File.Exists(path)) return new Dictionary<string, IReadOnlyList<ChatImageResourceRecord>>(StringComparer.OrdinalIgnoreCase);

        ChatImageResourceReadResult result;
        try
        {
            result = await new ChatImageResourceReader().ReadAsync(path, sqliteExecutable, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new Dictionary<string, IReadOnlyList<ChatImageResourceRecord>>(StringComparer.OrdinalIgnoreCase);
        }

        return result.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Stem))
            .GroupBy(item => item.Stem!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatImageResourceRecord>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static ChatImageIndexRecord ApplyResourceEvidence(ChatImageIndexRecord index,
        IReadOnlyDictionary<string, IReadOnlyList<ChatImageResourceRecord>> evidence,
        IReadOnlyCollection<string> candidatePaths)
    {
        var stem = Path.GetFileNameWithoutExtension(index.FileName);
        if (!evidence.TryGetValue(stem, out var resources) || resources.Count == 0) return index;
        var candidateSizes = candidatePaths.Where(File.Exists)
            .Select(path => new FileInfo(path).Length)
            .ToHashSet();
        var resource = resources.FirstOrDefault(item => item.Size is long size && candidateSizes.Contains(size)) ??
                       resources[0];
        return index with
        {
            ResourceStem = resource.Stem,
            ResourceType = resource.Type,
            ResourceSize = resource.Size,
            ResourceStatus = resource.Status,
            ResourceId = resource.ResourceId,
            ResourceMessageId = resource.MessageId,
            ResourceCandidates = resources
        };
    }

    private sealed record SourceFingerprint(long Size, DateTime LastWriteUtc, string Sha256)
    {
        public static async Task<SourceFingerprint> CreateAsync(string path, CancellationToken cancellationToken)
        {
            var info = new FileInfo(path);
            return new SourceFingerprint(info.Length, info.LastWriteTimeUtc,
                await Hashing.Sha256FileAsync(path, cancellationToken));
        }

        public async Task<bool> IsUnchangedAsync(string path, CancellationToken cancellationToken)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == Size && info.LastWriteTimeUtc == LastWriteUtc &&
                   string.Equals(await Hashing.Sha256FileAsync(path, cancellationToken), Sha256,
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record CandidateFailureContext(string Source, SourceFingerprint? Fingerprint,
        ChatImageVerification? Verification, string ErrorCode, string ErrorSummary);

    private sealed record CandidateCleanupFailure(string Source, SourceFingerprint Fingerprint,
        ChatImageVerification Verification, string ErrorSummary);

    private static CandidateFailureContext? SelectCandidateFailure(IReadOnlyList<CandidateFailureContext> failures) =>
        failures.LastOrDefault(item => item.Verification is { Passed: false }) ??
        failures.LastOrDefault(item => !string.Equals(item.ErrorCode, "candidate_processing_failed",
            StringComparison.Ordinal)) ??
        failures.LastOrDefault();

    private static string StageFailureCode(string stage) => stage switch
    {
        "source_fingerprint" => "source_fingerprint_failed",
        "decrypt" => "decrypt_failed",
        "decrypted_diagnostics" => "decrypted_diagnostics_failed",
        "normalize" => "normalize_failed",
        "normalized_diagnostics" => "normalized_diagnostics_failed",
        "validate" => "validation_failed",
        "source_guard" => "source_guard_failed",
        _ => "candidate_processing_failed"
    };

    private static string? CleanupCandidateFiles(string temporary, string normalized, bool keepNormalized)
    {
        string? failure = null;
        if (!keepNormalized) failure = DeleteFileWithRetry(normalized);
        if (!string.Equals(normalized, temporary, StringComparison.Ordinal))
            failure = CombineCleanupErrors(failure, DeleteFileWithRetry(temporary));
        return failure;
    }

    private static CandidateCleanupFailure? CleanupValidFiles(IEnumerable<(string Source, string Temporary,
        ChatImageVerification Verification, SourceFingerprint Fingerprint,
        ChatImageCandidateDiagnostic Diagnostic)> valid)
    {
        CandidateCleanupFailure? failure = null;
        foreach (var item in valid)
        {
            var error = DeleteFileWithRetry(item.Temporary);
            if (error is null) continue;

            item.Diagnostic.ErrorCode = "candidate_cleanup_failed";
            item.Diagnostic.ErrorSummary = error;
            failure = failure is null
                ? new CandidateCleanupFailure(item.Source, item.Fingerprint, item.Verification, error)
                : failure with { ErrorSummary = CombineCleanupErrors(failure.ErrorSummary, error)! };
        }
        return failure;
    }

    private static void ApplyCleanupFailure(ChatImageManifestItem item, CandidateCleanupFailure failure)
    {
        item.Status = "failed";
        item.CleanupStatus = "failed";
        item.CleanupSourceSize = failure.Fingerprint.Size;
        item.CleanupSourceSha256 = failure.Fingerprint.Sha256;
        item.CleanupVerification = failure.Verification;
        item.CleanupErrorCode = "candidate_cleanup_failed";
        item.CleanupErrorSummary = failure.ErrorSummary;
    }

    private static string? DeleteFileWithRetry(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return null;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
            }
            Thread.Sleep(25 * (attempt + 1));
        }
        return last?.GetType().Name ?? "FileStillExists";
    }

    private static string? DeleteDirectoryWithRetry(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return null;
            }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
            }
            Thread.Sleep(25 * (attempt + 1));
        }
        return last?.GetType().Name ?? "DirectoryStillExists";
    }

    private static string? CombineCleanupErrors(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "+" + second;

    private sealed record PayloadFingerprint(long Size, string Sha256, byte[] Prefix, string PrefixHex)
    {
        public static async Task<PayloadFingerprint> CreateAsync(string path, CancellationToken cancellationToken)
        {
            var prefix = new byte[32];
            int read;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             prefix.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                read = await stream.ReadAsync(prefix, cancellationToken);
            if (read != prefix.Length) Array.Resize(ref prefix, read);
            return new PayloadFingerprint(new FileInfo(path).Length,
                await Hashing.Sha256FileAsync(path, cancellationToken), prefix,
                Convert.ToHexString(prefix).ToLowerInvariant());
        }
    }

    private static string PayloadFormat(ReadOnlySpan<byte> prefix)
    {
        if (WxgfImageDecoder.IsWxgf(prefix)) return "wxgf";
        if (prefix.Length >= 3 && prefix[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff })) return "jpeg";
        if (prefix.Length >= 8 && prefix[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "png";
        if (prefix.Length >= 6 && (prefix[..6].SequenceEqual("GIF87a"u8) || prefix[..6].SequenceEqual("GIF89a"u8)))
            return "gif";
        if (prefix.Length >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        return "unknown";
    }
}
