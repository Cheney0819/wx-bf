namespace DesktopPet.Recovery;

public sealed record AppProcessIdentity(int ProcessId, string ExecutablePath);

public interface IAppProcessController
{
    Task<AppProcessIdentity> RestartAsync(CancellationToken cancellationToken);

    Task<AppProcessIdentity> RestartAsync(
        Func<CancellationToken, Task> beforeStart,
        CancellationToken cancellationToken);
}
