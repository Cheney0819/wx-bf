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
    {
        cancellationToken.ThrowIfCancellationRequested();
        Report(Steps[0], "正在定位安装目录…");
        var candidates = locator.Locate(commandLineDirectory);
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

        Report(Steps[1], "正在退出安装目录中的后台进程…");
        Report(Steps[2], "正在清理桌面、开始菜单和开机启动入口…");
        Report(Steps[3], "正在删除安装文件…");
        var result = await Task.Run(
            () => coordinator.Run(installation, TimeSpan.FromSeconds(10)),
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
