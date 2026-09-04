namespace Footprint.Worker;

public static class WorkerRunner
{
    public static bool TryGetPipeName(string[] args, out string pipeName)
    {
        var pipeIndex = Array.IndexOf(args, "--pipe");
        if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeIndex + 1]))
        {
            pipeName = string.Empty;
            return false;
        }

        pipeName = args[pipeIndex + 1];
        return true;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryGetPipeName(args, out _))
            return 64;

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
