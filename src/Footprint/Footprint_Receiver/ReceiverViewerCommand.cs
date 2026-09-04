using Footprint.Renderer;

namespace Footprint.Receiver;

public sealed class ReceiverViewerCommand(string archiveRoot)
{
    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!TryCreateRequest(args, out var runId, out var request))
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }

        try
        {
            await using var registry = new ReceiverViewerRegistry(archiveRoot);
            await registry.RestoreAsync(cancellationToken).ConfigureAwait(false);
            var response = await registry.HandleAsync(runId, request, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(response.Body).ConfigureAwait(false);
            return response.StatusCode is >= 200 and < 300 ? 0 : 4;
        }
        catch (ArgumentException)
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }
        catch (InvalidDataException)
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }
    }

    private static bool TryCreateRequest(IReadOnlyList<string> args, out string runId,
        out ViewerRequest request)
    {
        runId = string.Empty;
        request = null!;
        if (args.Count < 2) return false;
        runId = args[0];
        if (args[1] == "sessions" && args.Count == 2)
        {
            request = new ViewerRequest("GET", "/sessions", null);
            return true;
        }
        if (args[1] == "search" && args.Count >= 3)
        {
            request = new ViewerRequest("GET", "/search", string.Join(' ', args.Skip(2)));
            return true;
        }
        return false;
    }

    private static Task WriteUsageAsync(TextWriter error) => error.WriteLineAsync(
        "用法：Footprint_Receiver viewer <run-id> sessions | viewer <run-id> search <query>");
}
