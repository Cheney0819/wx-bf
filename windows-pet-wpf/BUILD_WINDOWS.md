# Windows 构建说明

当前候选版本为 `1.0.14`，目标架构为 `win-x64`。桌宠主程序以普通权限运行；安装器只在安装、升级或修复后台任务时请求一次管理员权限。

后台由两个独立 Worker 组成：

- `Background/Recovery/DesktopPet.Recovery.Worker.exe`：微信进程观察、密钥捕获、数据库快照和解密。
- `Background/DataSync/DesktopPet.DataSync.Worker.exe`：Parser、增量游标、Outbox、遥测和上传。

安装器注册当前交互用户的两个计划任务。Recovery 使用最高权限，DataSync 使用普通权限；桌宠窗口关闭后 Worker 继续运行，Windows 登录时自动恢复。旧扫描器、旧 watchdog 和旧解密入口不在发布目录中。

## GitHub Actions 打包

推荐使用仓库工作流 `.github/workflows/build-windows.yml`：

1. 在 GitHub Actions 中运行 `构建桌宠 Windows 安装包`。
2. Windows Runner 安装 .NET 8、Python、FFmpeg、Pester 和 Inno Setup。
3. 工作流运行 Background、Parser、卸载器测试和发布范围检查。
4. `build.ps1` 生成 framework-dependent `win-x64` 发布目录并编译中文安装包。
5. 下载 `桌宠-1.0.14` artifact，里面包含安装包、发布清单和 SHA-256 文件。

远程打包分支必须不包含整个 `server/` 目录；工作流中的 `verify-release-scope.ps1` 会在提交和工作树阶段阻止后端路径进入发布。

## Windows 本地打包

在项目根目录执行：

```powershell
choco install ffmpeg -y --no-progress
python -m pip install -r windows-parser\requirements-build.txt
.\build.ps1 -Runtime win-x64
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" windows-pet-wpf\DesktopPetSetup.iss
```

构建脚本会：

1. 发布 WPF、Recovery Worker 和 DataSync Worker。
2. 使用 PyInstaller 构建 `Background/Parser/wx_parser.exe`。
3. 将任务注册脚本复制到 `Background/register-background-tasks.ps1`。
4. 将 FFmpeg 只放入 `Background/Parser/ffmpeg.exe`。
5. 生成 `release-manifest.json` 并验证每个文件的长度和 SHA-256。
6. 拒绝旧 `wx_decrypt.exe`、`WeChatMonitor`、旧命令行入口和 `server/`。

发布目录为：

```text
windows-pet-wpf/bin/Release/net8.0-windows/win-x64/publish/
  DesktopPet.Wpf.exe
  Background/
    Recovery/DesktopPet.Recovery.Worker.exe
    DataSync/DesktopPet.DataSync.Worker.exe
    Parser/wx_parser.exe
    Parser/parser-install.json
    Parser/ffmpeg.exe
    register-background-tasks.ps1
  release-manifest.json
```

发布目录检查：

```powershell
.\windows-pet-wpf\tests\validate-release.ps1 `
  -PublishRoot .\windows-pet-wpf\bin\Release\net8.0-windows\win-x64\publish
```

## 真机验收

只安装最终安装包，不手工拼接 publish 文件夹：

1. 安装时出现一次 UAC，普通启动 WPF 不再出现 UAC。
2. 任务计划程序中存在 `JunjieeDesktopPet-Recovery` 和 `JunjieeDesktopPet-DataSync`。
3. Recovery 为当前用户最高权限，DataSync 为当前用户普通权限。
4. 关闭桌宠窗口后两个 Worker 继续运行；重新登录 Windows 后自动恢复。
5. 微信未登录时保持被动等待；登录后开始捕获和长期补齐，不出现旧 30 秒扫描提示。
6. 零 key 时最多静默重启两次，熔断后不无限重启。
7. 断网并重新联网，DataSync Outbox 继续上传且不重复入库。
8. 覆盖安装保留 `%LOCALAPPDATA%\JunjieeDesktopPet\Background` 下的 key、状态库和未完成任务。
9. 卸载后计划任务与相关进程消失，默认保留本地状态。

安装包大小超过 100 MiB 时，CI 会失败并列出最大的新增文件，防止重复打入 .NET Runtime 或旧工具。
