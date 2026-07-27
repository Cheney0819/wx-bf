using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery;

public static class RecoveryPolicy
{
    public const int MaximumActiveRestarts = 2;

    public static bool IsRestartSuppressed(RecoveryEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        return !epoch.IsActive ||
               epoch.ActiveRestartSuppressed ||
               epoch.Mode is RecoveryMode.KeyMaterialAvailable or RecoveryMode.CaptureCircuitOpen;
    }
}

public enum RecoveryActionKind
{
    CaptureCurrent,
    RestartAndCapture,
    RelaunchProcess,
    WaitPassively,
    PublishOutputs,
}

public sealed record RecoveryAction(
    RecoveryActionKind Kind,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<RecoveredDatabase> Databases,
    string? Reason,
    bool RequiredDatabasesComplete = true)
{
    public static RecoveryAction CaptureCurrent() =>
        new(RecoveryActionKind.CaptureCurrent, [], [], null);

    public static RecoveryAction RestartAndCapture() =>
        new(RecoveryActionKind.RestartAndCapture, [], [], null);

    public static RecoveryAction Relaunch(string reason) =>
        new(RecoveryActionKind.RelaunchProcess, [], [], reason);

    public static RecoveryAction Wait(string? reason = null) =>
        new(RecoveryActionKind.WaitPassively, [], [], reason);

    public static RecoveryAction Publish(
        IReadOnlyList<string> outputPaths,
        IReadOnlyList<RecoveredDatabase> databases,
        bool requiredDatabasesComplete = true) =>
        new(
            RecoveryActionKind.PublishOutputs,
            outputPaths,
            databases,
            Reason: null,
            requiredDatabasesComplete);
}
