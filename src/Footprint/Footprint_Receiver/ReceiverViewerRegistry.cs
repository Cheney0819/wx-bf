using System.Text.Json;
using Footprint.Receiver.Network;
using Footprint.Renderer;
using Microsoft.Data.Sqlite;

namespace Footprint.Receiver;

internal sealed class ReceiverViewerRegistry(string archiveRoot) : IAsyncDisposable
{
    private const string ArchivePrefix = "Footprint_Run_";
    private readonly string archiveRoot = Path.GetFullPath(archiveRoot);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, FootprintViewerHost> viewers = new(StringComparer.Ordinal);
    private bool disposed;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(archiveRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(archiveRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!TryGetRunId(name, out var runId)) continue;
            try
            {
                await PublishAsync(runId, directory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsInvalidArchive(exception))
            {
            }
        }
    }

    public async Task PublishAsync(string runId, string archiveDirectory,
        CancellationToken cancellationToken = default)
    {
        PackageIdentity.ValidateRunId(runId);
        var replacement = await FootprintViewerHost.OpenAsync(archiveDirectory, cancellationToken)
            .ConfigureAwait(false);
        FootprintViewerHost? previous = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            viewers.Remove(runId, out previous);
            viewers.Add(runId, replacement);
        }
        catch
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<ViewerResponse> HandleAsync(string runId, ViewerRequest request,
        CancellationToken cancellationToken = default)
    {
        PackageIdentity.ValidateRunId(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!viewers.TryGetValue(runId, out var viewer))
                return new ViewerResponse(404, "application/json; charset=utf-8",
                    "{\"error\":\"未找到 Viewer。\"}");
            return await viewer.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        FootprintViewerHost[] active;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            active = viewers.Values.ToArray();
            viewers.Clear();
        }
        finally
        {
            gate.Release();
        }
        foreach (var viewer in active) await viewer.DisposeAsync().ConfigureAwait(false);
    }

    private static bool TryGetRunId(string name, out string runId)
    {
        runId = string.Empty;
        if (name.Length != ArchivePrefix.Length + 32 ||
            !name.StartsWith(ArchivePrefix, StringComparison.Ordinal))
            return false;
        var suffix = name[ArchivePrefix.Length..];
        if (suffix.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return false;
        runId = suffix;
        return true;
    }

    private static bool IsInvalidArchive(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or SqliteException;
}
