# WPF Worker 切换与 Windows 发布设计

## 背景与决策

Windows 后台 Recovery、DataSync、Parser 与遥测链路已经完成实现和自动验证，但当前 WPF 仍以管理员身份启动旧 `watchdog -> monitor-only -> WeChatMonitor -> wx_decrypt.exe` 链路，现有构建脚本也没有发布两个新 Worker。因此，直接打包当前 WPF 得到的安装包无法验证新后台能力。

本阶段采用以下切换方案：WPF 作为普通权限前台入口，启动时请求运行安装器预先注册的两个独立计划任务；计划任务负责后台 Worker 的权限和常驻生命周期。新链路启用时一次性移除旧捕获链路，禁止新旧捕获器并行运行。

本设计是 `2026-07-25-windows-background-recovery-design.md` 的阶段 4 与阶段 5 交付设计，不改动已通过验证的 Worker 业务契约。

## 目标

- 安装后启动桌宠即可自动运行 Recovery 和 DataSync，无需用户手工操作。
- WPF 日常以普通权限运行，不在每次启动时弹出 UAC。
- Recovery 以当前交互用户的最高权限运行，DataSync 以当前交互用户的普通权限运行。
- 关闭桌宠窗口不停止后台 Worker；登录 Windows 后后台能力自动恢复。
- 新旧密钥捕获、解密、解析和上传链路不会同时运行。
- 安装、覆盖升级和卸载都能正确停止、替换、恢复或删除后台任务和进程。
- 产出可在 Windows x64 真机安装的候选 EXE，并提供构建内容和哈希证据。
- 远程打包分支及其提交不包含 `server/` 目录或本轮后端源码改动。
- 避免两个 Worker 各自携带完整 .NET Runtime，控制安装包体积。

## 非目标

- 不修改 Recovery 的两次静默重启预算、持久熔断和数据库续跑策略。
- 不修改 DataSync 的解析、Outbox、遥测或服务端协议语义。
- 不接入安卓端。
- 不在本阶段加入混淆、虚拟化或强反调试。
- 不提供免安装便携模式；需要最高权限的 Recovery 必须通过安装器注册的任务运行。

## 运行时架构

```text
Windows 登录
  |-- 计划任务 DesktopPet-Recovery  (当前用户，Highest)
  |      `-- Background/Recovery/DesktopPet.Recovery.Worker.exe
  |
  `-- 计划任务 DesktopPet-DataSync  (当前用户，Limited)
         `-- Background/DataSync/DesktopPet.DataSync.Worker.exe
                 `-- Background/Parser/wx_parser.exe

DesktopPet.Wpf.exe (普通权限)
  `-- 启动时对两个固定任务执行“如未运行则启动”
```

计划任务是 Worker 的唯一生产启动入口。WPF 不直接拼接任意可执行文件路径，不传入用户输入参数，也不负责重启循环。Worker 自身的单实例锁继续作为第二道防线。

WPF 启动时分别请求运行两个固定任务。任务已运行、请求重复或状态查询暂时失败都不阻止桌宠界面出现；失败只写入本地脱敏诊断。桌宠退出时不结束任务。安装器负责首次注册和立即启动任务，Windows 登录触发器负责后续常驻恢复。

## 权限与任务配置

安装器保持管理员权限，只在安装、升级或修复后台组件时使用提权。

- `JunjieeDesktopPet-Recovery` 使用当前交互用户、`InteractiveToken`、最高权限和登录触发器。
- `JunjieeDesktopPet-DataSync` 使用当前交互用户、`InteractiveToken`、普通权限和登录触发器。
- 两个任务的动作只指向 `{app}\Background\...` 下固定的 Worker 路径，工作目录固定为各自发布目录。
- 任务设置为已有实例运行时不启动新实例；意外退出后每分钟重试，单次登录最多自动重试三次，避免无限进程风暴。
- 任务注册失败时安装失败并回滚，不留下只有一半可用的后台环境。
- WPF 不再声明 `requireAdministrator`，不再包含 `runas` 自提权逻辑。

Recovery 和 DataSync 都使用 `CurrentUser` 范围的 DPAPI，因此两个任务必须运行在安装时的交互用户上下文，不能切换成 SYSTEM 或其他账户。

## WPF 切换

WPF 新增一个边界很小的后台启动器，职责仅为：

1. 使用固定任务名查询或请求启动两个计划任务。
2. 对“任务不存在”“权限配置错误”“启动命令失败”生成有界本地诊断。
3. 不等待 Worker 完成，不在界面进程内实现 Worker 监管循环。

以下旧行为一次性删除：

- `--watchdog` 和 `--monitor-only` 命令行模式。
- `DesktopPetWeChatWatchdog` 与 `DesktopPetWeChatMonitor` 互斥体。
- WPF 启动时的管理员自提升。
- WPF 对 `windows/WeChatMonitor.cs` 的编译引用。
- WPF 对旧 `wx_decrypt.exe` 的复制、启动和打包依赖。

旧源码可以保留在本地历史中供审计，但不再进入 WPF 编译图和发布目录。构建检查发现旧 `wx_decrypt.exe`、旧命令行入口或 `WeChatMonitor` 发布引用时直接失败。

## 发布目录

安装目录采用稳定、互不覆盖的子目录：

```text
{app}/
  DesktopPet.Wpf.exe
  WPF 依赖与 Assets/
  Background/
    Recovery/
      DesktopPet.Recovery.Worker.exe
      Recovery 依赖
    DataSync/
      DesktopPet.DataSync.Worker.exe
      DataSync 依赖
    Parser/
      wx_parser.exe
      parser-install.json
      Parser 运行依赖
    release-manifest.json
```

`parser-install.json` 使用相对路径描述 Parser 入口、允许的运行参数和发布版本。DataSync 启动时仍按现有规则校验该清单。

WPF 与两个 Worker 均发布为 `win-x64`、framework-dependent。目标机器缺失 .NET 8 Windows Desktop Runtime 时，安装器在注册任务前给出明确错误并中止。这样只依赖机器上的一份共享 Runtime，避免三个自包含应用重复携带 Runtime。

Parser 使用现有裁剪后的独立发布产物，只包含解析所需模块；不再构建或携带旧扫描、内存捕获和解密工具。FFmpeg 只保留一份，并由 Parser 清单引用。

## 构建与发布证据

`build.ps1` 负责按以下顺序生成暂存目录：

1. 还原并发布 WPF。
2. 发布 Recovery Worker 和 DataSync Worker 到各自子目录。
3. 构建裁剪后的 Parser，并生成 `parser-install.json`。
4. 放置单份 FFmpeg。
5. 生成 `release-manifest.json`，记录版本、RID、相对路径、大小和 SHA-256。
6. 执行必需文件、禁止文件、重复 Runtime 和绝对路径检查。

Inno Setup 只读取该暂存目录，不从源码树零散取文件。候选版本从 `1.0.13` 提升到 `1.0.14`，项目版本、安装器版本和产物名使用同一来源，避免手工漂移。

GitHub Actions 在 `windows-latest` 上执行完整测试、Release 构建、Inno 编译和静态安装包检查，上传：

- `桌宠-1.0.14-安装包.exe`
- `桌宠-1.0.14-release-manifest.json`
- `桌宠-1.0.14-sha256.txt`

工作流输出安装包大小。若相对上一候选包增长超过 20 MB，构建失败并列出最大的新增文件，防止无意中重新打入重复 Runtime 或旧工具。

## 安装、升级与卸载

### 安装

1. WPF、Worker、Parser 和发布清单写入 `{app}`。
2. 校验必需文件与发布清单。
3. 原子注册两个计划任务；任一步失败则删除本次创建的任务并让安装失败。
4. 启动两个任务。
5. 以原交互用户启动普通权限 WPF。

### 覆盖升级

1. 停止两个计划任务。
2. 终止 Recovery、DataSync、Parser、FFmpeg 和旧 WPF/旧解密器进程树。
3. 替换文件并校验发布清单。
4. 重新注册任务，以确保路径、权限和恢复策略与当前版本一致。
5. 启动两个任务和 WPF。

升级失败时恢复旧安装文件和旧任务定义。`%LOCALAPPDATA%\JunjieeDesktopPet\Background` 下的数据库、key vault、熔断预算、handoff 和 outbox 不被覆盖或清空。

### 卸载

卸载器先停止并删除两个计划任务，再终止相关进程并删除安装目录。默认继续保留本地状态；只有用户明确选择删除本地数据时，才删除 `%LOCALAPPDATA%\JunjieeDesktopPet`。

## 远程分支隔离

本地功能分支包含已验证的后端契约工作，但用户要求后端源码不进入远程打包分支。发布时不直接推送当前功能分支，而是从允许的远程基线创建专用 Windows 打包分支，只移植客户端、测试、文档和构建改动。

远程分支必须满足：

- 工作树中不存在 `server/`。
- 待推送提交中不存在本轮 `server/` 文件补丁。
- 工作流只使用客户端本地测试替身，不依赖仓库内后端源码。
- 推送前检查 `git diff` 文件清单和 `git rev-list` 对象可达性；发现后端路径即停止发布。

本地主工作区中用户故意删除的 `server/server.py` 和其他未提交文件保持原样。合并采用独立工作树和显式文件清单，不在脏主工作区执行会覆盖用户改动的操作。

## 测试与验收

### 自动测试

- WPF 启动器：固定任务名、重复启动幂等、任务缺失、命令失败、界面不被阻塞。
- 构建布局：两个 Worker、Parser 清单和发布清单存在；旧 `wx_decrypt.exe`、旧命令行入口和 `WeChatMonitor` 不存在。
- 安装脚本：两个任务的用户、权限、动作路径、工作目录、登录触发器和重试上限正确。
- 升级：旧进程树停止、状态目录保留、任务重新注册。
- 卸载：任务先删除、相关进程停止、本地状态默认保留。
- 原有 Background、DataSync、Recovery、Wx411、Parser、WPF 和卸载器测试全部通过。
- Release 构建零警告、`git diff --check` 通过，发布清单中的哈希与实际文件一致。

### Windows 真机验收

用户只测试最终候选安装包：

1. 安装时仅出现一次 UAC；之后普通启动 WPF 不再弹 UAC。
2. 启动桌宠后可观察到两个 Worker，Recovery 为提升权限，DataSync 为普通权限。
3. 关闭桌宠后两个 Worker 继续运行；重新登录 Windows 后自动恢复。
4. 微信未运行时 Recovery 被动等待；微信启动并登录后捕获，不恢复旧 30 秒扫描路径。
5. 零 key 时最多静默重启两次，熔断后不再无限重启；后续数据库监听和已有 key 补齐继续运行。
6. 断网、重启 DataSync、重新联网后 Outbox 继续上传且不重复入库。
7. 覆盖安装后状态、key、任务和未完成同步仍存在。
8. 卸载后任务与进程消失，默认保留本地状态。

## 回滚边界

新链路候选包不提供运行时开关回到旧捕获器，因为双链并存会产生重复捕获和不一致状态。出现阻断问题时，回滚整个安装包到冻结版本；状态库按向前兼容原则保留，旧版无法识别的新状态不参与其运行。

发布完成的判定是：远程打包分支无 `server/`，GitHub Actions 全绿，安装包、发布清单和 SHA-256 均已下载并核对。macOS 本机只负责源码和跨平台测试，不声称完成 Windows 安装生命周期验证。
