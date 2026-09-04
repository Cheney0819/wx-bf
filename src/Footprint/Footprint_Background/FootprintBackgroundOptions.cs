using Footprint.Core.Runtime;

namespace Footprint.Background;

public sealed record FootprintBackgroundOptions(
    string PipeName,
    bool TestMode,
    FootprintPaths Paths,
    string InstanceName)
{
    public const string DefaultPipeName = "Footprint_Background_Pipe_v1";
    public const string DefaultInstanceName = "Local\\Footprint_Background";

    public static FootprintBackgroundOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var testMode = args.Contains("--test-mode", StringComparer.Ordinal);
        if (!testMode)
        {
            return new FootprintBackgroundOptions(
                DefaultPipeName,
                false,
                FootprintPaths.ForCurrentUser(),
                DefaultInstanceName);
        }

        var root = ReadOption(args, "--test-root");
        return new FootprintBackgroundOptions(
            ReadOption(args, "--pipe") ?? DefaultPipeName,
            true,
            root is null ? FootprintPaths.ForCurrentUser() : FootprintPaths.ForRoot(root),
            ReadOption(args, "--instance-name") ?? DefaultInstanceName);
    }

    private static string? ReadOption(string[] values, string option)
    {
        var index = Array.IndexOf(values, option);
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }
}
