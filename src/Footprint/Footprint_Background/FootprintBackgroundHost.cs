using Footprint.Core.Runtime;
using Footprint.Core.State;

namespace Footprint.Background;

public sealed class FootprintBackgroundHost(
    FootprintPaths paths,
    IFootprintStateStore store,
    WindowsBackgroundProductionRuntime? productionRuntime = null)
{
    public async Task RunAsync(
        string pipeName, bool allowTestShutdown, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        await store.InitializeAsync(cancellationToken);
        var pipe = new FootprintPipeServer(pipeName, allowTestShutdown).RunAsync(cancellationToken);
        if (allowTestShutdown || productionRuntime is null)
        {
            await pipe;
            return;
        }
        await Task.WhenAll(pipe, productionRuntime.RunAsync(cancellationToken));
    }
}
