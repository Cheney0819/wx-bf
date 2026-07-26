using DesktopPet.Uninstaller.Core;

namespace DesktopPet.Uninstaller;

public interface IUninstallerInstallationLocator
{
    IReadOnlyList<InstallationCandidate> Locate(string? commandLineDirectory);
}

public interface IUninstallerCoordinator
{
    OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout);
}

public interface IProgressReportingUninstallerCoordinator : IUninstallerCoordinator
{
    OperationResult Run(
        InstallationCandidate installation,
        TimeSpan processTimeout,
        Action<UninstallCoordinatorStage, string> reportProgress);
}

public sealed record UninstallStatus(string Step, string Detail);

public sealed class UninstallerApplicationHost(
    IUninstallerInstallationLocator locator,
    IUninstallerCoordinator coordinator,
    Func<IReadOnlyList<InstallationCandidate>, CancellationToken, Task<InstallationCandidate?>>? selectCandidate = null)
{
    private static readonly string[] Steps =
    [
        "定位安装目录",
        "退出后台进程",
        "清理安装入口",
        "删除文件",
        "验证结果"
    ];

    public event EventHandler<UninstallStatus>? StatusChanged;

    public async Task<int> RunAsync(string? commandLineDirectory, CancellationToken cancellationToken)
        => await RunParsedAsync(
            string.IsNullOrWhiteSpace(commandLineDirectory)
                ? InstallDirectoryArgument.Absent
                : new InstallDirectoryArgument(InstallDirectoryArgumentState.Valid, commandLineDirectory),
            cancellationToken);

    public async Task<int> RunParsedAsync(InstallDirectoryArgument installDirectoryArgument, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(Steps[0], "正在定位安装目录…");
            if (installDirectoryArgument.State == InstallDirectoryArgumentState.Malformed)
            {
                Report(Steps[0], "--install-dir 参数缺少有效安装目录。已取消卸载。");
                return 1;
            }

            var candidates = locator.Locate(installDirectoryArgument.Directory);
            if (candidates.Count == 0)
            {
                Report(Steps[0], "未找到可卸载的桌宠安装目录。");
                return 1;
            }

            var installation = candidates.Count == 1
                ? candidates[0]
                : await SelectCandidateAsync(candidates, cancellationToken);
            if (installation is null)
            {
                Report(Steps[0], "未选择安装目录，已取消卸载。");
                return 1;
            }

            var result = await Task.Run(
                () => RunCoordinator(installation),
                cancellationToken);

            if (result.Succeeded)
            {
                Report(Steps[4], "卸载成功");
            }
            else
            {
                Report(Steps[4], "卸载失败");
                foreach (var message in result.Messages)
                {
                    Report(Steps[4], message);
                }
            }

            return result.Succeeded ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Report(Steps[4], "卸载已取消。");
            return 1;
        }
        catch (Exception exception)
        {
            Report(Steps[4], $"卸载失败：{exception.Message}");
            return 1;
        }
    }

    private OperationResult RunCoordinator(InstallationCandidate installation) =>
        coordinator is IProgressReportingUninstallerCoordinator progressCoordinator
            ? progressCoordinator.Run(installation, TimeSpan.FromSeconds(10), ReportCoordinatorProgress)
            : coordinator.Run(installation, TimeSpan.FromSeconds(10));

    private void ReportCoordinatorProgress(UninstallCoordinatorStage stage, string detail) =>
        Report(stage switch
        {
            UninstallCoordinatorStage.StopProcesses => Steps[1],
            UninstallCoordinatorStage.CleanupShortcuts => Steps[2],
            UninstallCoordinatorStage.RemoveFiles => Steps[3],
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        }, detail);

    private async Task<InstallationCandidate?> SelectCandidateAsync(
        IReadOnlyList<InstallationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (selectCandidate is null)
        {
            Report(Steps[0], "发现多个安装目录，请选择要卸载的路径。");
            return null;
        }

        Report(Steps[0], "发现多个安装目录，请选择要卸载的路径。");
        return await selectCandidate(candidates, cancellationToken);
    }

    private void Report(string step, string detail) =>
        StatusChanged?.Invoke(this, new UninstallStatus(step, detail));
}
