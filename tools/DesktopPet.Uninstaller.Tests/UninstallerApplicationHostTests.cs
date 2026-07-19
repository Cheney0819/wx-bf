using DesktopPet.Uninstaller;
using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class UninstallerApplicationHostTests
{
    [Fact]
    public async Task RunAsync_returns_one_when_no_candidate_exists()
    {
        var host = new UninstallerApplicationHost(new FakeLocator([]), new FakeCoordinator());

        Assert.Equal(1, await host.RunAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_runs_the_single_candidate_and_reports_all_uninstall_stages()
    {
        var candidate = new InstallationCandidate(@"C:\Pet", InstallKind.Direct, null);
        var coordinator = new FakeCoordinator();
        var host = new UninstallerApplicationHost(new FakeLocator([candidate]), coordinator);
        var statuses = new List<UninstallStatus>();
        host.StatusChanged += (_, status) => statuses.Add(status);

        var exitCode = await host.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(candidate, coordinator.Installation);
        Assert.Equal(
        [
            "定位安装目录",
            "退出后台进程",
            "清理安装入口",
            "删除文件",
            "验证结果"
        ], statuses.Select(status => status.Step));
    }

    [Fact]
    public async Task RunAsync_returns_one_and_reports_each_residue_when_uninstall_fails()
    {
        var candidate = new InstallationCandidate(@"C:\Pet", InstallKind.Direct, null);
        var coordinator = new FakeCoordinator
        {
            Result = OperationResult.Failure("安装目录仍存在。", "开始菜单快捷方式仍存在。")
        };
        var host = new UninstallerApplicationHost(new FakeLocator([candidate]), coordinator);
        var statuses = new List<UninstallStatus>();
        host.StatusChanged += (_, status) => statuses.Add(status);

        var exitCode = await host.RunAsync(null, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(
        ["卸载失败", "安装目录仍存在。", "开始菜单快捷方式仍存在。"],
            statuses.Where(status => status.Step == "验证结果").Select(status => status.Detail));
    }

    private sealed class FakeLocator(IReadOnlyList<InstallationCandidate> candidates) : IUninstallerInstallationLocator
    {
        public IReadOnlyList<InstallationCandidate> Locate(string? commandLineDirectory) => candidates;
    }

    private sealed class FakeCoordinator : IUninstallerCoordinator
    {
        public InstallationCandidate? Installation { get; private set; }
        public OperationResult Result { get; init; } = OperationResult.Success("Installation artifacts removed.");

        public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout)
        {
            Installation = installation;
            return Result;
        }
    }
}
