namespace DesktopPet.DataSync.Worker;

public enum DataSyncCommandMode
{
    Continuous,
    Once,
    Diagnose,
}

public static class DataSyncCommandLine
{
    public static DataSyncCommandMode Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return DataSyncCommandMode.Continuous;
        if (arguments.Count != 1)
            throw new ArgumentException("Worker accepts one fixed mode switch.", nameof(arguments));
        return arguments[0] switch
        {
            "--once" => DataSyncCommandMode.Once,
            "--diagnose" => DataSyncCommandMode.Diagnose,
            _ => throw new ArgumentException("Unknown Worker mode.", nameof(arguments)),
        };
    }
}
