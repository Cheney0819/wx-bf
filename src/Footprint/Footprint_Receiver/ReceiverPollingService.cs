using Footprint.Receiver.Configuration;
using Footprint.Receiver.Download;
using Footprint.Receiver.Internal;
using Footprint.Receiver.Mac;
using Footprint.Receiver.Network;
using Footprint.Receiver.Publication;
using Footprint.Receiver.State;
using System.Text.Json;
using Footprint.Archive;
using Footprint.Renderer;

namespace Footprint.Receiver;

public sealed class ReceiverPollingService(
    IReceiverApiClient api,
    IReceiverStateStore state,
    PackageDownloadService downloader,
    PackageExpander expander,
    string downloadRoot,
    string packagesRoot,
    string? archiveRoot = null,
    string? archiveScratchRoot = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly ReceiverViewerRegistry? viewerRegistry = archiveRoot is null ? null : new(archiveRoot);

    public static ReceiverPollingService CreateDefault(IReceiverApiClient api, IReceiverStateStore state, string downloadRoot, string packagesRoot,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string? archiveRoot = null, string? archiveScratchRoot = null) =>
        new(api, state, new PackageDownloadService(), new PackageExpander(), downloadRoot, packagesRoot,
            archiveRoot, archiveScratchRoot, delay);

    public async Task RunAsync(ReceiverOptions options, string token, CancellationToken cancellationToken)
    {
        ReceiverToken.Validate(token);
        await RestoreViewersAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await PollOnceAsync(options, token, cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (IsRetryablePollingFailure(exception)) { }
            await (delay ?? Task.Delay)(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryablePollingFailure(Exception exception)
    {
        if (IsAuthenticationFailure(exception)) return false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
                return true;
        }
        return false;
    }

    private static bool IsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
                return true;
        }
        return false;
    }

    public async Task PollOnceAsync(ReceiverOptions options, string token, CancellationToken cancellationToken)
    {
        ReceiverToken.Validate(token);
        var pending = await api.GetPendingAsync(token, cancellationToken).ConfigureAwait(false);
        foreach (var group in pending.GroupBy(run => run.RunId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = group.ToArray();
            PendingRun? run = null;
            try
            {
                foreach (var candidate in items)
                {
                    PackageIdentity.Validate(candidate.RunId, candidate.PackageLength, candidate.PackageSha256);
                    PackageIdentity.ValidateDeviceId(candidate.SourceDeviceId);
                }
                run = items[0];
                if (items.Any(candidate => candidate.PackageLength != run.PackageLength ||
                    !string.Equals(candidate.PackageSha256, run.PackageSha256, StringComparison.Ordinal) ||
                    !string.Equals(candidate.SourceDeviceId, run.SourceDeviceId, StringComparison.Ordinal)))
                {
                    await TryRecordFailureAsync(group.Key, token, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await ProcessRunAsync(run, token, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsAuthenticationFailure(exception)) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                await TryRecordFailureAsync(run?.RunId ?? group.Key, token, cancellationToken).ConfigureAwait(false);
            }
        }
        await ReplayStatusesAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessRunAsync(PendingRun run, string token, CancellationToken cancellationToken)
    {
        using (var lease = RunLease.TryAcquire(packagesRoot, run.RunId))
        {
            if (lease is null) return;
            var received = await state.HasReceivedAsync(run, cancellationToken).ConfigureAwait(false);
            if (!received)
            {
                var expanded = await DownloadAndExpandAsync(run, token, cancellationToken).ConfigureAwait(false);
                if (archiveRoot is not null)
                {
                    var archive = await BuildArchiveAsync(expanded, run, repairInvalidArchive: false,
                        cancellationToken).ConfigureAwait(false);
                    await PublishViewerAsync(run.RunId, archive.ArchiveDirectory, cancellationToken).ConfigureAwait(false);
                    await state.RecordCompletedIntentAsync(run, cancellationToken).ConfigureAwait(false);
                }
                else await state.RecordReceivedIntentAsync(run, cancellationToken).ConfigureAwait(false);
            }
            else if (archiveRoot is not null)
            {
                var expanded = Path.Combine(Path.GetFullPath(packagesRoot), run.RunId);
                if (!Directory.Exists(expanded))
                    expanded = await DownloadAndExpandAsync(run, token, cancellationToken).ConfigureAwait(false);
                var archive = await BuildArchiveAsync(expanded, run, repairInvalidArchive: true,
                    cancellationToken).ConfigureAwait(false);
                await PublishViewerAsync(run.RunId, archive.ArchiveDirectory, cancellationToken).ConfigureAwait(false);
                await state.RecordCompletedIntentAsync(run, cancellationToken).ConfigureAwait(false);
            }
            else await state.EnsureReceivedStatusAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        }
        await ReplayStatusesAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadAndExpandAsync(PendingRun run, string token,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadRoot);
        var packagePath = Path.Combine(downloadRoot, $"Footprint_Run_{run.RunId}.zip");
        PackageDownloadService.RemoveStaleSiblingPartials(packagePath);
        await using var response = await api.DownloadAsync(run.RunId, token, cancellationToken).ConfigureAwait(false);
        await downloader.DownloadAsync(run, response, packagePath, cancellationToken).ConfigureAwait(false);
        return await expander.ExpandAndPublishAsync(packagePath, run.RunId, packagesRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ArchiveArtifact> BuildArchiveAsync(string expanded, PendingRun run,
        bool repairInvalidArchive, CancellationToken cancellationToken)
    {
        var archives = Path.GetFullPath(archiveRoot!);
        var scratch = Path.GetFullPath(archiveScratchRoot ?? Path.Combine(packagesRoot, "..", "ArchiveScratch"));
        var pipeline = new LocalArchivePipeline();
        try
        {
            return await pipeline.BuildAsync(expanded, archives, scratch, run.SourceDeviceId,
                    "Footprint_Run_" + run.RunId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException) when (repairInvalidArchive)
        {
            var quarantined = await QuarantineInvalidArchiveAsync(expanded, archives, cancellationToken)
                .ConfigureAwait(false);
            if (quarantined is null) throw;
            try
            {
                var rebuilt = await pipeline.BuildAsync(expanded, archives, scratch, run.SourceDeviceId,
                    "Footprint_Run_" + run.RunId, cancellationToken).ConfigureAwait(false);
                DeleteQuarantinedArchive(quarantined.Value);
                return rebuilt;
            }
            catch
            {
                RestoreQuarantinedArchive(quarantined.Value);
                throw;
            }
        }
    }

    private static async Task<(string Target, string Quarantine, bool Directory)?> QuarantineInvalidArchiveAsync(
        string expanded, string archives,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(expanded), "Footprint_CaptureManifest.json");
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("run_id", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("接收包清单 RunId 无效。");
        var sourceId = value.GetString()!;
        if (sourceId.Length != "Footprint_Run_".Length + 32 ||
            !sourceId.StartsWith("Footprint_Run_", StringComparison.Ordinal) ||
            sourceId["Footprint_Run_".Length..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("接收包清单 RunId 无效。");
        Directory.CreateDirectory(archives);
        RejectRepairLink(new DirectoryInfo(archives));
        var target = Path.GetFullPath(Path.Combine(archives, sourceId));
        var expected = archives.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar + sourceId;
        if (!string.Equals(target, expected, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("档案修复路径无效。");
        var quarantine = target + ".repair-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(target))
        {
            RejectRepairLink(new DirectoryInfo(target));
            Directory.Move(target, quarantine);
            return (target, quarantine, true);
        }
        if (File.Exists(target))
        {
            RejectRepairLink(new FileInfo(target));
            File.Move(target, quarantine);
            return (target, quarantine, false);
        }
        return null;
    }

    private static void DeleteQuarantinedArchive((string Target, string Quarantine, bool Directory) quarantined)
    {
        if (quarantined.Directory) Directory.Delete(quarantined.Quarantine, true);
        else File.Delete(quarantined.Quarantine);
    }

    private static void RestoreQuarantinedArchive((string Target, string Quarantine, bool Directory) quarantined)
    {
        if (Directory.Exists(quarantined.Target)) Directory.Delete(quarantined.Target, true);
        else if (File.Exists(quarantined.Target)) File.Delete(quarantined.Target);
        if (quarantined.Directory) Directory.Move(quarantined.Quarantine, quarantined.Target);
        else File.Move(quarantined.Quarantine, quarantined.Target);
    }

    private static void RejectRepairLink(FileSystemInfo item)
    {
        item.Refresh();
        if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("档案修复目标不能是链接。");
    }

    private async Task PublishViewerAsync(string runId, string archiveDirectory,
        CancellationToken cancellationToken)
    {
        await viewerRegistry!.PublishAsync(runId, archiveDirectory, cancellationToken).ConfigureAwait(false);
    }

    public Task RestoreViewersAsync(CancellationToken cancellationToken = default) =>
        viewerRegistry?.RestoreAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task<ViewerResponse> HandleViewerAsync(string runId, ViewerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (viewerRegistry is not null)
            return await viewerRegistry.HandleAsync(runId, request, cancellationToken).ConfigureAwait(false);
        PackageIdentity.ValidateRunId(runId);
        return new ViewerResponse(404, "application/json; charset=utf-8", "{\"error\":\"未找到 Viewer。\"}");
    }

    public async ValueTask DisposeAsync()
    {
        if (viewerRegistry is not null) await viewerRegistry.DisposeAsync().ConfigureAwait(false);
    }

    private async Task TryRecordFailureAsync(string runId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PackageIdentity.ValidateRunId(runId);
            await state.RecordStatusIntentAsync(runId, "失败", cancellationToken).ConfigureAwait(false);
            await ReplayStatusesAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (IsAuthenticationFailure(exception)) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task ReplayStatusesAsync(string token, CancellationToken cancellationToken)
    {
        var updates = await state.GetPendingStatusReportsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await api.ReportStatusAsync(update, token, cancellationToken).ConfigureAwait(false);
                await state.AcknowledgeStatusAsync(update, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsAuthenticationFailure(exception)) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
    }
}
