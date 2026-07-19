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
    public async Task RunAsync_reports_coordinator_stages_only_when_they_are_reached()
    {
        var candidate = new InstallationCandidate(@"C:\Pet", InstallKind.Direct, null);
        var coordinator = new ProgressFakeCoordinator();
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
    public async Task RunAsync_does_not_report_later_stages_after_process_shutdown_failure()
    {
        var candidate = new InstallationCandidate(@"C:\Pet", InstallKind.Direct, null);
        var coordinator = new ProgressFakeCoordinator
        {
            StopAfter = UninstallCoordinatorStage.StopProcesses,
            Result = OperationResult.Failure("Target process remains: PID 42")
        };
        var host = new UninstallerApplicationHost(new FakeLocator([candidate]), coordinator);
        var statuses = new List<UninstallStatus>();
        host.StatusChanged += (_, status) => statuses.Add(status);

        var exitCode = await host.RunAsync(null, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(statuses, status => status.Step is "清理安装入口" or "删除文件");
    }

    [Fact]
    public async Task RunAsync_returns_one_and_reports_exception_when_coordinator_throws()
    {
        var candidate = new InstallationCandidate(@"C:\Pet", InstallKind.Direct, null);
        var host = new UninstallerApplicationHost(
            new FakeLocator([candidate]),
            new ThrowingCoordinator(new InvalidOperationException("coordinator crashed")));
        var statuses = new List<UninstallStatus>();
        host.StatusChanged += (_, status) => statuses.Add(status);

        var exitCode = await host.RunAsync(null, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(statuses, status =>
            status.Step == "验证结果" && status.Detail.Contains("coordinator crashed", StringComparison.Ordinal));
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

    private sealed class ProgressFakeCoordinator : IProgressReportingUninstallerCoordinator
    {
        public InstallationCandidate? Installation { get; private set; }
        public UninstallCoordinatorStage? StopAfter { get; init; }
        public OperationResult Result { get; init; } = OperationResult.Success("Installation artifacts removed.");

        public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout)
        {
            Installation = installation;
            return Result;
        }

        public OperationResult Run(
            InstallationCandidate installation,
            TimeSpan processTimeout,
            Action<UninstallCoordinatorStage, string> reportProgress)
        {
            Installation = installation;
            foreach (var stage in Enum.GetValues<UninstallCoordinatorStage>())
            {
                reportProgress(stage, $"Reached {stage}.");
                if (stage == StopAfter)
                {
                    break;
                }
            }

            return Result;
        }
    }

    private sealed class ThrowingCoordinator(Exception exception) : IUninstallerCoordinator
    {
        public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout) => throw exception;
    }
}
