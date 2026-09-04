using System.Diagnostics;
using Footprint.Core.Ipc;

namespace Footprint;

public sealed class FootprintBootstrapRuntime : IFootprintBootstrapRuntime
{
    private const string DefaultPipeName = "Footprint_Background_Pipe_v1";

    private readonly string _pipeName;
    private readonly string[] _backgroundArguments;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<string> _applicationBaseDirectory;

    public FootprintBootstrapRuntime()
        : this(DefaultPipeName, () => AppContext.BaseDirectory, [], Process.Start)
    {
    }

    internal FootprintBootstrapRuntime(
        Func<ProcessStartInfo, Process?> processStarter,
        Func<string> applicationBaseDirectory)
        : this(DefaultPipeName, applicationBaseDirectory, [], processStarter)
    {
    }

    internal FootprintBootstrapRuntime(
        string pipeName,
        string applicationBaseDirectory,
        IReadOnlyList<string> backgroundArguments,
        Func<ProcessStartInfo, Process?> processStarter)
        : this(pipeName, () => applicationBaseDirectory, backgroundArguments, processStarter)
    {
    }

    private FootprintBootstrapRuntime(
        string pipeName,
        Func<string> applicationBaseDirectory,
        IReadOnlyList<string> backgroundArguments,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        _pipeName = pipeName;
        _backgroundArguments = backgroundArguments.ToArray();
        _processStarter = processStarter;
        _applicationBaseDirectory = applicationBaseDirectory;
    }

    public async Task EnsureBackgroundAsync(CancellationToken cancellationToken)
    {
        if (await CanPingAsync(cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();

        var applicationBaseDirectory = _applicationBaseDirectory();
        var executable = Path.Combine(applicationBaseDirectory, "Footprint_Background.exe");
        if (!File.Exists(executable)) return;
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = applicationBaseDirectory
        };
        foreach (var argument in _backgroundArguments) startInfo.ArgumentList.Add(argument);
        _ = _processStarter(startInfo);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await CanPingAsync(cancellationToken)) return;
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<bool> CanPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await new FootprintPipeClient(_pipeName)
                .PingAsync(TimeSpan.FromMilliseconds(300), cancellationToken);
            return response.Status == "running";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
