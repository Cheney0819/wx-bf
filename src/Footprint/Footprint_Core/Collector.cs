using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Footprint.Core.Capture;

namespace Footprint.Core;

public enum CollectionStage
{
    Runtime = 1, Version = 2, Hook = 3, Capture = 4, Snapshot = 5, Verify = 6, Decompress = 7,
    PlaintextExport = 8, ChatImageRecovery = 9, Report = 10
}

public sealed record CollectionProgress(CollectionStage Stage, string Message, double Fraction);
public sealed record CollectionResult(string OutputDirectory, string ManifestPath, string ReportPath, SessionManifest Manifest);

public sealed class Collector
{
    private readonly Assembly _runtimeAssembly;

    public Collector(Assembly? runtimeAssembly = null) =>
        _runtimeAssembly = runtimeAssembly ?? Assembly.GetEntryAssembly() ?? typeof(Collector).Assembly;

    public event EventHandler<CollectionProgress>? Progress;

    public async Task<CollectionResult> RunAsync(CancellationToken cancellationToken)
    {
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Footprint", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        RuntimeEnvironment? runtime = null;
        var weixinWasStarted = false;

        try
        {
            Report(CollectionStage.Runtime, "检查内嵌运行时", 0.02);
            runtime = await new RuntimeBootstrapper(_runtimeAssembly).EnsureAsync(cancellationToken);
            Report(CollectionStage.Version, "定位微信并验证固定版本", 0.12);
            var installation = WeixinLocator.Locate();
            var catalogSelection = new ProfileCatalog(LoadProfileForAdmission)
                .Select(installation.DllPath, runtime.ProfilePaths);
            var selection = catalogSelection.Selection;
            if (!selection.Accepted || !selection.MayControlProcess)
                throw new InvalidOperationException(selection.MessageZh);
            var profile = selection.Profile!;
            runtime = runtime with { ProfilePath = catalogSelection.ProfilePath! };

            if (!await WeixinLocator.RequestExitAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), cancellationToken))
                throw new InvalidOperationException("微信在正常关闭超时后仍无法被自动终止，请结束全部微信进程后重试。");

            Report(CollectionStage.Hook, "启动微信并在初始化前安装 Hook", 0.23);
            var captureDirectory = Path.Combine(output, "capture");
            weixinWasStarted = true;
            var capture = await RunHostAsync(runtime, "capture", installation.ExecutablePath, captureDirectory, null,
                cancellationToken);
            if (capture.ExitCode != 0)
                throw new InvalidOperationException("采集进程失败：" + CaptureFailureMessage(captureDirectory, capture));

            Report(CollectionStage.Capture, "整理 path/tag/key/db* 映射", 0.36);
            var captureResult = LoadCapture(captureDirectory);
            if (captureResult.Bindings.Count == 0) throw new InvalidOperationException("没有形成满足三边界一致性的数据库映射。");

            if (!await WeixinLocator.RequestExitAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), cancellationToken))
                throw new InvalidOperationException("采集完成后微信仍无法被自动终止，未制作数据库快照。");
            weixinWasStarted = false;

            var manifest = new SessionManifest { DllSha256 = profile.DllSha256 };
            manifest.Ambiguities.AddRange(captureResult.Ambiguities);
            var keyPaths = LoadProtectedKeyPaths(captureDirectory);
            Report(CollectionStage.Snapshot, "复制数据库、WAL 和 SHM 稳定快照", 0.48);
            for (var index = 0; index < captureResult.Bindings.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = captureResult.Bindings[index];
                var databaseDirectory = Path.Combine(output, "databases", $"{index:D3}-{SafeName(Path.GetFileName(binding.Path))}");
                var snapshot = await StableSnapshotter.CreateAsync(binding.Path, databaseDirectory, 3, cancellationToken);
                keyPaths.TryGetValue(binding.KeySha256, out var protectedPath);
                manifest.Databases.Add(new DatabaseManifest
                {
                    Path = binding.Path,
                    Tag = binding.Tag,
                    DbPointer = binding.DbPointer,
                    PathFromDb = binding.PathFromDb,
                    KeySha256 = binding.KeySha256,
                    KeyLength = binding.KeyLength,
                    ProtectedKeyPath = protectedPath is null ? null : Path.GetRelativePath(output, protectedPath),
                    PageSize = binding.PageSize,
                    Compatibility = binding.Compatibility,
                    Snapshot = snapshot
                });
            }

            Report(CollectionStage.Verify, "使用 SQLCipher 4.1.0 严格验证每个快照", 0.61);
            var verifier = new SqlCipherVerifier();
            foreach (var database in manifest.Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!database.Snapshot.Stable || database.ProtectedKeyPath is null)
                {
                    database.Verification.Reason = "快照不稳定或缺少 DPAPI 密钥文件。";
                    continue;
                }

                var protectedPath = Path.Combine(output, database.ProtectedKeyPath);
                var key = ProtectedKeyStore.UnprotectFromFile(protectedPath);
                var verificationDirectory = Path.Combine(output, ".verification",
                    Guid.NewGuid().ToString("N"));
                try
                {
                    var verificationPath = await VerificationSnapshotCopy.CreateAsync(database.Snapshot,
                        Path.GetFileName(database.Path), verificationDirectory, cancellationToken);
                    var verdict = await verifier.VerifyAsync(runtime.SqlCipherExecutable, verificationPath, key,
                        database.Compatibility, database.PageSize, "4.1.0", cancellationToken);
                    database.Verification.Accepted = verdict.Accepted;
                    database.Verification.Reason = verdict.Reason;
                    database.Verification.Trials = verdict.Trials.ToList();
                    if (verdict.Accepted)
                    {
                        database.Compatibility = verdict.Compatibility ?? database.Compatibility;
                        database.PageSize = verdict.PageSize ?? database.PageSize;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    VerificationSnapshotCopy.Delete(verificationDirectory);
                }
            }

            var decompressionDirectory = Path.Combine(output, "decompression");
            var firstLaunchExportDirectory = Path.Combine(captureDirectory, "runtime-export");
            if (File.Exists(Path.Combine(firstLaunchExportDirectory, "decompression-summary.json")))
            {
                ApplyFirstLaunchDecompressionSummary(firstLaunchExportDirectory, decompressionDirectory,
                    manifest.Databases);
            }
            var approved = manifest.Databases.Where(database => database.Verification.Accepted).ToArray();
            var pending = manifest.Databases.Where(database =>
                database.Verification.Accepted && !database.Decompression.Completed).ToArray();
            if (pending.Length > 0)
            {
                var retained = approved.Length - pending.Length;
                Report(CollectionStage.Decompress,
                    retained > 0 ? $"沿用首次启动已保留结果 {retained} 个，并再次启动微信处理剩余 {pending.Length} 个" :
                        "再次启动微信并调用连接内 wcdb_decompress", 0.76);
                var requestPath = Path.Combine(output, "decompression-request.json");
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
                {
                    batch_timeout_ms = 30000,
                    databases = pending.Select(database => new
                    {
                        path = database.Path,
                        tag = database.Tag,
                        key_sha256 = database.KeySha256,
                        key_length = database.KeyLength,
                        page_size = database.PageSize,
                        compatibility = database.Compatibility
                    })
                }, TargetProfile.JsonOptions), cancellationToken);
                weixinWasStarted = true;
                var decompression = await RunHostAsync(runtime, "decompress", installation.ExecutablePath,
                    decompressionDirectory, requestPath, cancellationToken);
                if (decompression.ExitCode != 0)
                {
                    foreach (var database in pending) database.Decompression.Failures.Add(decompression.StandardError.Trim());
                }
                else
                {
                    ApplyDecompressionSummary(decompressionDirectory, pending);
                }
            }
            else if (approved.Length > 0)
            {
                Report(CollectionStage.Decompress, "沿用首次启动已保留结果，无需再次启动微信", 0.76);
                await WeixinLocator.EnsureRunningAsync(installation.ExecutablePath, cancellationToken);
                weixinWasStarted = true;
            }
            else
            {
                Report(CollectionStage.Decompress, "没有通过严格验证的数据库，跳过解压并恢复微信", 0.76);
                await WeixinLocator.EnsureRunningAsync(installation.ExecutablePath, cancellationToken);
                weixinWasStarted = true;
            }

            Report(CollectionStage.PlaintextExport, "导出并校验标准 SQLite 明文数据库", 0.86);
            var plaintextExporter = new PlainDbExporter();
            await plaintextExporter.ExportAsync(output, manifest, runtime.SqlCipherExecutable, cancellationToken,
                (databaseName, stage, fraction) => Report(CollectionStage.PlaintextExport,
                    string.IsNullOrEmpty(databaseName) ? "写入明文导出清单" : $"{databaseName}：{stage}",
                    0.84 + fraction * 0.08));

            Report(CollectionStage.ChatImageRecovery, "Windows 源端不执行 FFmpeg，聊天图片待 Mac 接收端恢复", 0.93);
            DeferChatImagesToMac(manifest);

            Report(CollectionStage.Report, "生成 HTML、JSON 与 JSONL 结果", 0.97);
            var manifestPath = Path.Combine(output, "session-manifest.json");
            await ManifestWriter.WriteAsync(manifest, manifestPath, cancellationToken);
            var reportPath = Path.Combine(output, "index.html");
            await File.WriteAllTextAsync(reportPath, ReportRenderer.Render(manifest), cancellationToken);
            Report(CollectionStage.Report, "完成", 1.0);
            return new CollectionResult(output, manifestPath, reportPath, manifest);
        }
        catch (OperationCanceledException)
        {
            if (weixinWasStarted) _ = await WeixinLocator.RequestExitAsync(TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            throw;
        }
        catch
        {
            if (weixinWasStarted) _ = await WeixinLocator.RequestExitAsync(TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            throw;
        }
    }

    internal static TargetProfile LoadProfileForAdmission(string profilePath)
    {
        try
        {
            return TargetProfile.Load(profilePath);
        }
        catch (ProfileFormatException exception)
        {
            var publicException = new InvalidOperationException(exception.MessageZh, exception);
            publicException.Data[nameof(ProfileFormatException.ErrorCode)] = exception.ErrorCode;
            throw publicException;
        }
    }

    private static CaptureBuildResult LoadCapture(string captureDirectory)
    {
        var binder = new CaptureBinder(TimeSpan.FromSeconds(10), File.Exists);
        var path = Path.Combine(captureDirectory, "capture-events.jsonl");
        if (!File.Exists(path)) return new CaptureBuildResult([], [new CaptureAmbiguity("Capture event file is missing.", [])]);
        foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))) binder.Add(CaptureJson.Parse(line).Event);
        return binder.Build();
    }

    private static Dictionary<string, string> LoadProtectedKeyPaths(string captureDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(captureDirectory, "capture-events.jsonl");
        foreach (var line in File.ReadLines(path))
        {
            var item = CaptureJson.Parse(line);
            if (item.Event.KeySha256 is not null && item.ProtectedKeyPath is not null)
                result[item.Event.KeySha256] = item.ProtectedKeyPath;
        }
        return result;
    }

    private static string CaptureFailureMessage(string captureDirectory, ProcessResult capture)
    {
        var summaryPath = Path.Combine(captureDirectory, "capture-summary.json");
        if (File.Exists(summaryPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(summaryPath));
                var count = document.RootElement.TryGetProperty("connection_count", out var connections)
                    ? connections.GetInt32() : 0;
                var counts = document.RootElement.TryGetProperty("boundary_counts", out var boundaries)
                    ? string.Join(", ", boundaries.EnumerateObject().Select(item => $"{item.Name}={item.Value.GetInt32()}"))
                    : "无边界统计";
                return $"连接映射数={count}；Hook 事件：{counts}。";
            }
            catch (JsonException)
            {
            }
        }
        var diagnostic = capture.StandardError.Trim();
        return diagnostic.Length > 0 ? diagnostic : $"Host exit={capture.ExitCode}, timedOut={capture.TimedOut}.";
    }

    private static void ApplyDecompressionSummary(string directory, IReadOnlyCollection<DatabaseManifest> databases)
    {
        var validation = LoadDecompressionSummary(directory, databases);
        if (!validation.IsValid)
        {
            RejectDecompressionSummary(databases, validation);
            return;
        }
        ApplyValidatedDecompressionSummary(validation.Summary, databases);
    }

    private static void ApplyFirstLaunchDecompressionSummary(string sourceDirectory, string destinationDirectory,
        IReadOnlyCollection<DatabaseManifest> databases)
    {
        var validation = LoadDecompressionSummary(sourceDirectory, databases);
        if (!validation.IsValid)
        {
            RejectDecompressionSummary(databases, validation);
            return;
        }
        CopyDirectory(sourceDirectory, destinationDirectory);
        ApplyValidatedDecompressionSummary(validation.Summary, databases);
    }

    private static DecompressionSummaryValidationResult LoadDecompressionSummary(string directory,
        IReadOnlyCollection<DatabaseManifest> databases)
    {
        var path = Path.Combine(directory, "decompression-summary.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > DecompressionSummaryValidator.MaximumBytes)
                return new DecompressionSummaryValidationResult(false,
                    DecompressionSummaryValidator.SummaryInvalidCode,
                    "Frida 解压摘要缺失、为空或超过大小限制。", default);
            var bindings = databases.Select(database => new DecompressionSummaryBinding(database.Path, database.Tag,
                database.KeySha256, database.KeyLength)).ToArray();
            return DecompressionSummaryValidator.Validate(File.ReadAllBytes(path), bindings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DecompressionSummaryValidationResult(false,
                DecompressionSummaryValidator.SummaryInvalidCode,
                $"Frida 解压摘要读取失败：{exception.GetType().Name}。", default);
        }
    }

    private static void ApplyValidatedDecompressionSummary(JsonElement summary,
        IReadOnlyCollection<DatabaseManifest> databases)
    {
        var results = summary.GetProperty("database_results");
        foreach (var result in results.EnumerateArray())
        {
            var databasePath = result.GetProperty("path").GetString()!;
            var tag = result.GetProperty("tag").GetInt32();
            var keySha256 = result.GetProperty("key_sha256").GetString()!;
            var keyLength = result.GetProperty("key_length").GetInt32();
            var database = databases.Single(item =>
                DecompressionSummaryValidator.DatabasePathsEqual(item.Path, databasePath) && item.Tag == tag &&
                item.KeyLength == keyLength && string.Equals(item.KeySha256, keySha256,
                    StringComparison.OrdinalIgnoreCase));
            var failureReason = DecompressionSummaryValidator.ValidatedDatabaseFailureReason(result);
            var failed = failureReason is not null;
            database.Decompression.Completed = !failed;
            database.Decompression.Records = result.GetProperty("records").GetInt64();
            database.Decompression.Failures.RemoveAll(message => string.Equals(message,
                DecompressionSummaryValidator.InvalidMessageZh, StringComparison.Ordinal));
            if (failed)
            {
                var failure = $"frida_decompression_optional_database_failed: {databasePath}; " +
                              failureReason;
                if (!database.Decompression.Failures.Contains(failure, StringComparer.Ordinal))
                    database.Decompression.Failures.Add(failure);
            }
        }
        ApplyCountMap(summary, "schema_counts", databases, (manifest, count) => manifest.SchemaObjects = count);
        ApplyCountMap(summary, "table_counts", databases, (manifest, count) => manifest.Tables = count);
        ApplyCountMap(summary, "compressed_column_counts", databases,
            (manifest, count) => manifest.CompressedColumns = count);
        var schemaPath = summary.TryGetProperty("schema_path", out var schema) ? schema.GetString() : null;
        var compressionPath = summary.TryGetProperty("compression_records_path", out var compression) ? compression.GetString() : null;
        var recordsPath = summary.TryGetProperty("records_path", out var records) ? records.GetString() : null;
        var tableStatsPath = summary.TryGetProperty("table_stats_path", out var tableStats) ? tableStats.GetString() : null;
        var tablePreviewPath = summary.TryGetProperty("table_preview_path", out var tablePreview) ? tablePreview.GetString() : null;
        foreach (var database in databases)
        {
            database.Decompression.SchemaPath = RelativeDecompressionPath(schemaPath);
            database.Decompression.CompressionRecordsPath = RelativeDecompressionPath(compressionPath);
            database.Decompression.RecordsPath = RelativeDecompressionPath(recordsPath);
            database.Decompression.TableStatsPath = RelativeDecompressionPath(tableStatsPath);
            database.Decompression.TablePreviewPath = RelativeDecompressionPath(tablePreviewPath);
        }
    }

    private static void RejectDecompressionSummary(IEnumerable<DatabaseManifest> databases,
        DecompressionSummaryValidationResult validation)
    {
        var failure = $"{validation.Code}: {validation.MessageZh}";
        foreach (var database in databases)
        {
            if (!database.Decompression.Failures.Contains(failure, StringComparer.Ordinal))
                database.Decompression.Failures.Add(failure);
        }
    }

    private static void ApplyCountMap(JsonElement root, string property, IReadOnlyCollection<DatabaseManifest> databases,
        Action<DecompressionManifest, long> apply)
    {
        if (!root.TryGetProperty(property, out var counts)) return;
        foreach (var item in counts.EnumerateObject())
        {
            var database = databases.FirstOrDefault(candidate =>
                DecompressionSummaryValidator.DatabasePathsEqual(candidate.Path, item.Name));
            if (database is not null) apply(database.Decompression, item.Value.GetInt64());
        }
    }

    private static string? RelativeDecompressionPath(string? value) => value is null ? null : Path.Combine("decompression", value)
        .Replace(Path.DirectorySeparatorChar, '/');

    internal static void DeferChatImagesToMac(SessionManifest manifest)
    {
        manifest.ChatImages = new ChatImageManifest { Status = "not_run" };
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static Task<ProcessResult> RunHostAsync(RuntimeEnvironment runtime, string command, string executable,
        string output, string? requestPath, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            runtime.FridaHostScript, command, "--exe", executable, "--agent", runtime.AgentScript,
            "--profile", runtime.ProfilePath, "--output", output, "--max-seconds", "180"
        };
        if (command == "capture") arguments.AddRange(["--idle-seconds", "10"]);
        if (command == "decompress") arguments.AddRange(["--idle-seconds", "30"]);
        if (requestPath is not null) arguments.AddRange(["--request", requestPath]);
        return ProcessRunner.RunAsync(runtime.PythonExecutable, arguments, null, TimeSpan.FromMinutes(4),
            cancellationToken, runtime.Root);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private void Report(CollectionStage stage, string message, double fraction) =>
        Progress?.Invoke(this, new CollectionProgress(stage, message, fraction));
}
