namespace DesktopPet.Background.Infrastructure;

public sealed record BackgroundPaths(
    string Root,
    string RecoveryRoot,
    string RecoveryDatabase,
    string RecoveryVault,
    string RecoveryCriticalSnapshot,
    string RecoveryGenerations,
    string DataSyncRoot,
    string SyncDatabase,
    string HandoffRoot,
    string HandoffReady,
    string HandoffAccepted)
{
    public static BackgroundPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Local application data directory is unavailable.");

        return ForRoot(Path.Combine(
            localAppData,
            "JunjieeDesktopPet",
            "Background"));
    }

    public static BackgroundPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalizedRoot = Path.GetFullPath(root);
        var recoveryRoot = Path.Combine(normalizedRoot, "Recovery");
        var dataSyncRoot = Path.Combine(normalizedRoot, "DataSync");
        var handoffRoot = Path.Combine(normalizedRoot, "Handoff");

        return new BackgroundPaths(
            normalizedRoot,
            recoveryRoot,
            Path.Combine(recoveryRoot, "recovery.db"),
            Path.Combine(recoveryRoot, "Vault"),
            Path.Combine(recoveryRoot, "critical-state.dpapi"),
            Path.Combine(recoveryRoot, "Generations"),
            dataSyncRoot,
            Path.Combine(dataSyncRoot, "sync.db"),
            handoffRoot,
            Path.Combine(handoffRoot, "ready"),
            Path.Combine(handoffRoot, "accepted"));
    }
}
