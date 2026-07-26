namespace DesktopPet.Recovery.Worker;

public enum WorkerCommandMode
{
    Continuous,
    Once,
    Diagnose,
}

public static class WorkerCommandLine
{
    public static WorkerCommandMode Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return WorkerCommandMode.Continuous;
        if (arguments.Count != 1)
            throw new ArgumentException("Worker accepts at most one fixed mode switch.", nameof(arguments));
        return arguments[0] switch
        {
            "--once" => WorkerCommandMode.Once,
            "--diagnose" => WorkerCommandMode.Diagnose,
            _ => throw new ArgumentException("Unknown worker mode switch.", nameof(arguments)),
        };
    }
}
