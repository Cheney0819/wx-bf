# WPF Worker 切换与 Windows 发布实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 WPF 桌宠切换到已验证的 Recovery/DataSync 常驻 Worker，并生成不包含 `server/` 的可安装 Windows x64 候选包。

**Architecture:** WPF 以普通权限运行，只通过固定的 `schtasks.exe /Run /TN` 请求两个安装器注册的计划任务。Recovery 与 DataSync 作为独立 framework-dependent Worker 常驻，安装器负责任务权限、登录触发、升级停止和卸载清理；构建脚本将三个 .NET 应用、Parser 与发布清单放进稳定的 `Background/` 布局。

**Tech Stack:** C# / .NET 8 / WPF / Microsoft.Extensions.Hosting / xUnit / PowerShell / Inno Setup / PyInstaller / GitHub Actions。

## Global Constraints

- 远程打包分支及其工作树不包含整个 `server/` 目录。
- WPF 日常运行普通权限，Recovery 计划任务使用当前交互用户的 `Highest`，DataSync 使用 `Limited`。
- 每次捕获最多消耗两次静默微信重启；Worker 重启、桌宠退出或计时经过不恢复预算。
- 关闭 WPF 不停止 Worker；Windows 登录触发两个 Worker 自动恢复。
- 新旧捕获链路不得并行；WPF 不再编译或发布 `WeChatMonitor.cs`、`--watchdog`、`--monitor-only` 或旧 `wx_decrypt.exe`。
- .NET 应用使用 `win-x64` framework-dependent 发布，目标机需要 .NET 8 Windows Desktop Runtime。
- 不接触安卓端，不修改已通过验证的 Recovery/DataSync 业务契约。
- 每个任务先写测试，确认失败，再写最小实现，确认通过后单独提交。

---

### Task 1: 固定后台任务启动契约

**Files:**
- Create: `windows-background/src/DesktopPet.Background.Launcher/DesktopPet.Background.Launcher.csproj`
- Create: `windows-background/src/DesktopPet.Background.Launcher/BackgroundTaskActivation.cs`
- Modify: `windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj`
- Create: `windows-background/tests/DesktopPet.Background.Tests/BackgroundTaskActivationTests.cs`
- Modify: `windows-background/DesktopPet.Background.sln`

**Interfaces:**
- `BackgroundTaskNames.Recovery` 与 `BackgroundTaskNames.DataSync` 是唯一生产任务名。
- `IScheduledTaskRunner.RunAsync(string taskName, CancellationToken)` 返回 `ScheduledTaskRunResult(int ExitCode, string StandardError)`。
- `ScheduledTaskLauncher.ActivateAllAsync(CancellationToken)` 并发运行两个固定任务并返回两个 `BackgroundTaskActivationResult`；它只接受内部常量，不暴露任意任务名入口。

- [ ] **Step 1: 写失败测试**

在 `BackgroundTaskActivationTests.cs` 中加入以下测试和内存 Runner：

```csharp
[Fact]
public async Task ActivateAllRunsExactlyTheTwoFixedTasks()
{
    var runner = new RecordingTaskRunner();
    var launcher = new ScheduledTaskLauncher(runner);

    var results = await launcher.ActivateAllAsync(CancellationToken.None);

    Assert.Equal(
        [BackgroundTaskNames.Recovery, BackgroundTaskNames.DataSync],
        runner.TaskNames.OrderBy(static name => name));
    Assert.All(results, result => Assert.True(result.Succeeded));
}

[Fact]
public async Task OneTaskFailureIsReturnedWithoutBlockingTheOther()
{
    var runner = new RecordingTaskRunner(
        BackgroundTaskNames.Recovery,
        new ScheduledTaskRunResult(1, "missing"));
    var results = await new ScheduledTaskLauncher(runner)
        .ActivateAllAsync(CancellationToken.None);

    var recovery = Assert.Single(results, result => result.TaskName == BackgroundTaskNames.Recovery);
    var dataSync = Assert.Single(results, result => result.TaskName == BackgroundTaskNames.DataSync);
    Assert.False(recovery.Succeeded);
    Assert.True(dataSync.Succeeded);
}
```

`RecordingTaskRunner` 记录任务名并让指定任务返回失败，其余任务返回 `new(0, string.Empty)`。

- [ ] **Step 2: 运行失败测试**

Run:

```bash
dotnet test windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj --filter FullyQualifiedName~BackgroundTaskActivationTests
```

Expected: 编译失败，因为启动契约类型尚不存在。

- [ ] **Step 3: 写最小实现**

在 `BackgroundTaskActivation.cs` 中实现：

```csharp
public static class BackgroundTaskNames
{
    public const string Recovery = "JunjieeDesktopPet-Recovery";
    public const string DataSync = "JunjieeDesktopPet-DataSync";
}

public sealed record ScheduledTaskRunResult(int ExitCode, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IScheduledTaskRunner
{
    Task<ScheduledTaskRunResult> RunAsync(string taskName, CancellationToken cancellationToken);
}

public sealed record BackgroundTaskActivationResult(
    string TaskName,
    bool Succeeded,
    int ExitCode,
    string Error);

public sealed class ScheduledTaskLauncher(IScheduledTaskRunner runner)
{
    public async Task<IReadOnlyList<BackgroundTaskActivationResult>> ActivateAllAsync(
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            ActivateAsync(BackgroundTaskNames.Recovery, cancellationToken),
            ActivateAsync(BackgroundTaskNames.DataSync, cancellationToken));
        return results;
    }

    private async Task<BackgroundTaskActivationResult> ActivateAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(taskName, cancellationToken);
        return new(taskName, result.Succeeded, result.ExitCode, result.StandardError);
    }
}
```

添加 `SchtasksRunner`，使用 `ProcessStartInfo { FileName = "schtasks.exe", ArgumentList = { "/Run", "/TN", taskName }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }`，等待最多 10 秒并在超时后杀掉子进程。生产代码只从 `ScheduledTaskLauncher` 调用，不接收用户输入的任务名。

- [ ] **Step 4: 运行通过测试**

Run 同一 `dotnet test` 命令。Expected: 两个测试 PASS，解决方案无新增警告。

- [ ] **Step 5: 提交**

```bash
git add windows-background/src/DesktopPet.Background.Infrastructure/BackgroundTaskActivation.cs windows-background/tests/DesktopPet.Background.Tests windows-background/DesktopPet.Background.sln
git commit -m "feat: add fixed background task launcher"
```

### Task 2: 切除旧 WPF 入口并接入启动请求

**Files:**
- Modify: `windows-pet-wpf/App.xaml.cs`
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/app.manifest`
- Create: `windows-pet-wpf/BackgroundTaskBootstrap.cs`
- Create: `windows-background/src/DesktopPet.Background.Launcher/BackgroundActivationLog.cs`

**Interfaces:**
- `BackgroundTaskBootstrap.Start()` 创建 `SchtasksRunner` 和 `ScheduledTaskLauncher`，后台触发 `ActivateAllAsync`，不阻塞 WPF 窗口创建。
- 启动结果写入 `%LOCALAPPDATA%\JunjieeDesktopPet\Background\Launcher\activation.ndjson`，单文件超过 128 KiB 时保留最新 64 KiB。

- [ ] **Step 1: 写失败测试**

在 `BackgroundTaskActivationTests.cs` 增加测试：结果序列化包含任务名、退出码和错误文本；日志超过上限后小于 128 KiB 且保留最后一条。先把 `BackgroundActivationLog` 引用写入测试。

- [ ] **Step 2: 运行失败测试**

```bash
dotnet test windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj --filter FullyQualifiedName~BackgroundTaskActivationTests
```

Expected: 编译失败，因为日志类型尚不存在。

- [ ] **Step 3: 实现 WPF 切换**

将 `App.OnStartup` 收敛为：先 `base.OnStartup(e)`，调用 `BackgroundTaskBootstrap.Start()`，再创建并显示 `MainWindow`。删除 `EnsureElevatedOrExit`、`StartWatchdogProcess`、`StartHiddenMonitorProcess`、`EnsureWatchdogBackgroundProcess`、`WatchMonitorLoopAsync`、`IsMonitorRunning`、两个互斥体和 `--watchdog/--monitor-only` 分支。保留 WPF 原有界面生命周期。

删除项目文件中的：

```xml
<None Include="wx_decrypt.exe" ... />
<Compile Include="..\windows\WeChatMonitor.cs" Link="WeChatMonitor.cs" />
```

将 `app.manifest` 的 `requestedExecutionLevel` 改为 `asInvoker`。为 WPF 添加对无第三方依赖的 `DesktopPet.Background.Launcher` 的 ProjectReference，不添加对 Infrastructure、Recovery 或 DataSync Worker 的直接业务引用，避免把 SQLite 依赖拖入 WPF 发布目录。

- [ ] **Step 4: 运行测试与静态旧链检查**

```bash
dotnet test windows-background/DesktopPet.Background.sln
rg -n "runas|requireAdministrator|--watchdog|--monitor-only|WeChatMonitor|DesktopPetWeChatWatchdog|DesktopPetWeChatMonitor|wx_decrypt" windows-pet-wpf
```

Expected: 全部后台测试通过；第二条命令无输出（安装器停止旧进程的名称检查除外，随后 Task 4 会更新它）。

- [ ] **Step 5: 提交**

```bash
git add windows-pet-wpf windows-background/src/DesktopPet.Background.Launcher windows-background/tests/DesktopPet.Background.Tests windows-background/DesktopPet.Background.sln
git commit -m "feat: switch WPF to background workers"
```

### Task 3: 实现计划任务注册、修复和移除脚本

**Files:**
- Create: `windows-pet-wpf/register-background-tasks.ps1`
- Create: `windows-pet-wpf/tests/register-background-tasks.Tests.ps1`
- Modify: `build.ps1` (在发布阶段复制脚本到 `Background/`)

**Interfaces:**
- `register-background-tasks.ps1 -Mode Install -InstallRoot <path>` 注册或覆盖两个固定任务。
- `register-background-tasks.ps1 -Mode Remove` 停止并删除两个固定任务。
- 脚本从 `[System.Security.Principal.WindowsIdentity]::GetCurrent().Name` 获取当前用户，不接受外部用户、命令行或可执行文件路径。

- [ ] **Step 1: 写失败的 PowerShell 静态测试**

`register-background-tasks.Tests.ps1` 读取脚本文本并断言包含 `JunjieeDesktopPet-Recovery`、`JunjieeDesktopPet-DataSync`、`RunLevel Highest`、`RunLevel Limited`、`AtLogOn`、`RestartCount 3`、`RestartInterval (New-TimeSpan -Minutes 1)` 和 `Register-ScheduledTask`，并断言不包含 `Invoke-Expression`、`Start-Process` 的外部参数拼接和 `param(...UserId...)` 这种允许外部注入账户的接口。脚本内部使用当前身份创建 `-UserId $currentUser` 是必需行为。

- [ ] **Step 2: 运行失败测试**

```powershell
Invoke-Pester windows-pet-wpf/tests/register-background-tasks.Tests.ps1 -Output Detailed
```

Expected: 文件不存在导致失败。

- [ ] **Step 3: 实现脚本**

脚本使用 `Set-StrictMode -Version Latest` 和 `$ErrorActionPreference = "Stop"`。`Install` 模式先删除已有任务，创建两个 `New-ScheduledTaskAction`，分别使用 `New-ScheduledTaskPrincipal -LogonType Interactive -RunLevel Highest/Limited`，使用 `New-ScheduledTaskTrigger -AtLogOn -User $currentUser` 和 `New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable`。第二个任务失败时删除第一个并抛出异常。`Remove` 模式先 `Stop-ScheduledTask`，再 `Unregister-ScheduledTask -Confirm:$false`，任务不存在时忽略该错误。

把脚本作为 `Background\register-background-tasks.ps1` 复制到 WPF 发布目录，供 Inno 的 `[Run]` 和 `[UninstallRun]` 调用。

- [ ] **Step 4: 运行通过测试**

```powershell
Invoke-Pester windows-pet-wpf/tests/register-background-tasks.Tests.ps1 -Output Detailed
```

Expected: 静态测试 PASS。

- [ ] **Step 5: 提交**

```bash
git add windows-pet-wpf/register-background-tasks.ps1 windows-pet-wpf/tests windows-pet-wpf/DesktopPet.Wpf.csproj
git commit -m "feat: add scheduled worker task registration"
```

### Task 4: 更新 Inno 安装、升级、卸载生命周期

**Files:**
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`
- Modify: `windows-pet-wpf/uninstall.ps1`
- Modify: `tools/DesktopPet.Uninstaller.Core/ProcessShutdownService.cs`
- Modify: `tools/DesktopPet.Uninstaller.Tests/ProcessShutdownServiceTests.cs`

**Interfaces:**
- 安装完成后调用 `Background\register-background-tasks.ps1 -Mode Install -InstallRoot "{app}"`，注册失败使安装器返回非零。
- 卸载前调用同一脚本的 `Remove` 模式，然后停止安装目录内 Worker、Parser、FFmpeg 和 WPF 进程。

- [ ] **Step 1: 写失败测试**

扩展卸载器进程测试，使安装目录内的 `DesktopPet.Recovery.Worker.exe`、`DesktopPet.DataSync.Worker.exe` 和 `wx_parser.exe` 都被选入停止集合；安装目录外同名进程不被选入。为 Inno 文本增加静态断言：包含两个 Worker 进程名、任务脚本、`[UninstallRun]` 和 `StopProcessTree`。

- [ ] **Step 2: 运行失败测试**

```bash
dotnet test tools/DesktopPet.Uninstaller.Tests/DesktopPet.Uninstaller.Tests.csproj --filter FullyQualifiedName~ProcessShutdownServiceTests
```

Expected: 新增进程断言失败。

- [ ] **Step 3: 实现生命周期**

在 Inno `CurStepChanged(ssInstall)` 中先执行 `schtasks.exe /End /TN "JunjieeDesktopPet-Recovery"` 和 DataSync，随后终止旧 WPF、两个 Worker、`wx_parser.exe`、`wx_decrypt.exe` 和 `ffmpeg.exe`。新增 `[Run]` 的隐藏等待项注册任务，新增 `[UninstallRun]` 的隐藏等待项移除任务。更新 `uninstall.ps1` 的停止列表，保留对旧 `wx_decrypt.exe` 的兼容清理。

在卸载器进程目录过滤测试中复用现有 `InstallPathPolicy`，不按全局进程名杀进程。

- [ ] **Step 4: 运行通过测试**

```bash
dotnet test tools/DesktopPet.Uninstaller.Tests/DesktopPet.Uninstaller.Tests.csproj
```

Expected: 全部卸载器测试 PASS。

- [ ] **Step 5: 提交**

```bash
git add windows-pet-wpf/DesktopPetSetup.iss windows-pet-wpf/uninstall.ps1 tools/DesktopPet.Uninstaller.Core tools/DesktopPet.Uninstaller.Tests
git commit -m "feat: manage workers during install and uninstall"
```

### Task 5: 重构 Windows 发布布局并生成清单

**Files:**
- Modify: `build.ps1`
- Modify: `windows-parser/build-parser.ps1`
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`
- Create: `windows-pet-wpf/tests/validate-release.ps1`

**Interfaces:**
- `build.ps1 -Runtime win-x64` 生成 `windows-pet-wpf/bin/Release/net8.0-windows/win-x64/publish`。
- 发布目录包含 `Background/Recovery/DesktopPet.Recovery.Worker.exe`、`Background/DataSync/DesktopPet.DataSync.Worker.exe`、`Background/Parser/wx_parser.exe`、`Background/Parser/parser-install.json`、WPF Assets、单份 FFmpeg 和 `release-manifest.json`。
- `validate-release.ps1 -PublishRoot <path>` 验证必需文件、禁止文件、相对路径、哈希和大小阈值，并以非零退出码报告失败。

- [ ] **Step 1: 写失败的布局验证**

在 `validate-release.ps1` 中先写固定清单和断言；执行时使用当前旧 publish 目录，预期因缺少 `Background/Recovery`、`Background/DataSync` 和 `release-manifest.json` 失败。

- [ ] **Step 2: 运行失败验证**

```powershell
& windows-pet-wpf/tests/validate-release.ps1 -PublishRoot windows-pet-wpf/bin/Release/net8.0-windows/win-x64/publish
```

Expected: 输出缺失项并返回非零。

- [ ] **Step 3: 实现构建布局**

让 `build.ps1` 清理旧 `wx_decrypt.exe` 和旧 resources，发布 WPF 到临时目录，再分别执行：

```powershell
dotnet publish windows-background/src/DesktopPet.Recovery.Worker/DesktopPet.Recovery.Worker.csproj -c Release -r $Runtime --self-contained false -o "$PublishDir/Background/Recovery"
dotnet publish windows-background/src/DesktopPet.DataSync.Worker/DesktopPet.DataSync.Worker.csproj -c Release -r $Runtime --self-contained false -o "$PublishDir/Background/DataSync"
& windows-parser/build-parser.ps1 -OutputRoot "$ArtifactsRoot/parser"
```

将 Parser 的目录复制到 `Background/Parser`，将 `register-background-tasks.ps1` 复制到 `Background`。去掉旧 WPF 根目录 `wx_decrypt.exe` 的必需检查。保留单份 `ffmpeg.exe`，放到 `Background/Parser` 并在清单中记录其哈希。

`release-manifest.json` 使用 `System.Text.Json` 或 PowerShell `ConvertTo-Json` 记录版本 `1.0.14`、RID、每个相对文件的长度和 SHA-256。禁止绝对路径、`server/`、`WeChatMonitor`、`--watchdog`、`--monitor-only` 和第二份 .NET Runtime。将 Inno `[Files]` 源改成统一 publish 目录。

让 `windows-parser/build-parser.ps1` 只负责 Parser 和 `parser-install.json`，不再把两个 Worker 合并到 Parser 根目录，避免重复构建和路径混乱。

- [ ] **Step 4: 运行通过验证**

在 Windows Runner 或安装 .NET/PowerShell 的环境执行：

```powershell
pwsh -File build.ps1 -Runtime win-x64
& windows-pet-wpf/tests/validate-release.ps1 -PublishRoot windows-pet-wpf/bin/Release/net8.0-windows/win-x64/publish
```

Expected: 发布清单哈希全匹配，禁止项为零，生成目录不携带重复 Runtime。

- [ ] **Step 5: 提交**

```bash
git add build.ps1 windows-parser/build-parser.ps1 windows-pet-wpf/DesktopPet.Wpf.csproj windows-pet-wpf/DesktopPetSetup.iss windows-pet-wpf/tests/validate-release.ps1
git commit -m "build: package background workers separately"
```

### Task 6: 更新版本、工作流和远程分支发布检查

**Files:**
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`
- Modify: `.github/workflows/build-windows.yml`
- Create: `.github/workflows/verify-release-scope.ps1`

**Interfaces:**
- 工作流在 Windows Runner 上运行全部 .NET/Python 测试、发布验证、Inno 编译和产物哈希核对。
- `verify-release-scope.ps1` 检查 `git diff --name-only $BaseSha $HeadSha` 和当前工作树，任何 `server/` 路径直接失败。

- [ ] **Step 1: 写失败的 scope 检查**

使用临时 Git 仓库 fixture，创建 `server/sample.py` 改动后调用脚本，断言退出码非零；没有 `server/` 改动时断言退出码为零。

- [ ] **Step 2: 运行失败检查**

```powershell
& .github/workflows/verify-release-scope.ps1 -BaseSha HEAD~1 -HeadSha HEAD
```

Expected: 当前功能分支含后端路径时失败，防止直接推送它。

- [ ] **Step 3: 实现版本与 CI**

统一 WPF 项目、Inno `MyAppVersion`、文件版本和 artifact 名为 `1.0.14`。工作流先执行 `dotnet test windows-background/DesktopPet.Background.sln`、`dotnet test tools/DesktopPet.Uninstaller.sln`、Python Parser 测试和 scope 检查，再执行 `build.ps1`、发布验证和 Inno Setup。上传安装包、manifest、SHA-256 和构建日志。加入 20 MiB 相对上一候选包的大小门禁，并在日志中列出最大的十个文件。

在远程发布前从允许的远程基线创建独立打包分支，只移植本客户端提交和文档；明确删除 `server/`，执行 `git diff --name-only` 和 `git ls-tree -r --name-only HEAD server` 双重检查后再推送。主工作区中用户的未提交删除和未跟踪文件不参与该操作。

- [ ] **Step 4: 运行 CI 本地等价检查**

```bash
dotnet test windows-background/DesktopPet.Background.sln
dotnet test tools/DesktopPet.Uninstaller.sln
python -m pytest windows-parser/tests -q
git diff --check
```

Expected: 全部通过；Windows Runner 再完成 Inno 和安装器静态检查。

- [ ] **Step 5: 提交**

```bash
git add windows-pet-wpf/DesktopPet.Wpf.csproj windows-pet-wpf/DesktopPetSetup.iss .github/workflows/build-windows.yml .github/workflows/verify-release-scope.ps1
git commit -m "ci: build scoped Windows worker release"
```

### Task 7: 合并、触发 Windows 构建并核对候选包

**Files:**
- No source changes unless a verification failure identifies a concrete defect.
- Artifact: GitHub Actions `桌宠-1.0.14-安装包.exe`, `桌宠-1.0.14-release-manifest.json`, `桌宠-1.0.14-sha256.txt`.

- [ ] **Step 1: 验证合并前状态**

确认功能分支干净、全部测试通过、提交文件清单无 `server/`，再将允许提交合并到专用 Windows 发布分支。遇到主工作区脏文件时只使用独立 worktree，不执行清理或恢复。

- [ ] **Step 2: 触发 GitHub Actions**

```bash
gh workflow run build-windows.yml --repo Cheney0819/wx-bf --ref release/windows-worker-1.0.14
gh run watch --repo Cheney0819/wx-bf --exit-status
```

Expected: Windows job 全绿，上传三个候选产物。

- [ ] **Step 3: 下载并核对**

```bash
gh run download <run-id> --repo Cheney0819/wx-bf --name 桌宠-1.0.14
sha256sum 桌宠-1.0.14-安装包.exe
```

将本地 SHA-256 与 artifact 清单比较，检查安装包中存在两个 Worker、Parser 清单和任务脚本，不存在 `server/`、旧 `WeChatMonitor` 和旧 `wx_decrypt.exe`。

- [ ] **Step 4: 记录真机测试入口**

输出安装包绝对路径、SHA-256、构建运行 ID、版本和简化验收顺序。真机测试只针对这个候选安装包，不再让用户手工拼装 publish 目录。
