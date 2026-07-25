# Windows 桌宠常驻恢复与同步重构设计

## 目标

将已冻结的 RC9 恢复能力渐进接入 Windows .NET 8 WPF 桌宠，以两个职责明确的常驻后台 Worker 替换现有 `watchdog -> monitor-only -> WeChatMonitor -> wx_decrypt.exe` 链路。

改造后必须满足：

- 用户安装时只完成一次管理员授权，日常启动桌宠不再弹 UAC。
- 桌宠启动后自动激活后台能力；关闭桌宠界面后后台继续常驻，重新登录 Windows 后自动恢复。
- 对当前微信进程先捕获一轮；零 key 时最多静默重启微信两次，总计最多三轮主动捕获。
- 任一数据库进入 pending 或获得任一有效 key 后立即停止主动重启，不为剩余数据库重复重启微信。
- 两次重启后仍为零 key 时持久化熔断；Worker 崩溃、重启或计时经过都不会自动补充重启预算。
- 后台持续监听数据库新增与换代，对已有 key 的数据库自动生成稳定副本、解密、解析和上传。
- 断网、服务端错误、临时文件不一致或 Worker 崩溃不会丢失已完成的进度。
- 保留消息、联系人、收藏、图片、语音解析以及现有服务端同步语义。
- 删除已被替代的旧扫描、密钥捕获、解密、微信重启、watchdog 和重复解析代码。
- 安卓端不在本次范围内。

## 非目标

- 本次不将所有 Python 解析一次性重写为 C#。
- 本次不更改服务端 API 的业务语义；只在客户端增加可靠投递和协议版本。
- 本次不以强反调试、反 dump 或虚拟化保护作为首要交付。
- 本次不让主 WPF 界面继续以管理员权限运行。

## 总体架构

运行时有三个角色，其中只有两个后台角色常驻：

```text
DesktopPet.Wpf                     普通权限，可随时关闭
    | status/control IPC
    +---------------------+----------------------+
                          |                      |
DesktopPet.Recovery.Worker               DesktopPet.DataSync.Worker
最高权限计划任务                     普通权限计划任务
    | DatabaseReady manifest              | durable job claim
    +------------------------------------>|
                                         |-- wx_parser.exe (按任务启动)
                                         |-- encrypted outbox
                                         +-- HTTPS upload
```

### DesktopPet.Wpf

- 保留桌宠、语音、AI 交互和托盘界面。
- 以普通权限运行，不再执行 `runas`。
- 首次启动时请求任务计划程序启动两个已注册 Worker，该操作不弹 UAC。
- 只读取脱敏状态，只能发送白名单控制指令。
- 窗口退出时不停止 Worker。

### DesktopPet.Recovery.Worker

- 引用从 RC9 冻结源快照导入的 `Wx411.Core`，不引用 RC9 WinForms 界面和证据打包 UI。
- 独占拥有进程观察、早鸟附加、key 捕获、微信重启、稳定快照和 SQLCipher 解密职责。
- 以有限状态机驱动，不使用固定间隔重新扫描整棵目录。
- 保存 key vault、数据库 catalog、重启预算、熔断状态、任务代次和最近错误。
- 产出经 SQLite 完整性校验的临时明文副本和原子 `DatabaseReady` manifest。
- 不持有服务端 token，不解析聊天语义，不上传数据。

### DesktopPet.DataSync.Worker

- 以普通权限运行，不读取和不接收 key。
- 先将 Recovery 原子发布的 handoff manifest 幂等导入自有任务库，再以带租约的事务领取解析任务，崩溃后过期租约可重领。
- 为每个任务启动裁剪后的 `wx_parser.exe`，通过版本化 JSON manifest 传入解密数据库路径和输出目录。
- 验证 Parser 输出 schema 后，在同一事务中写入增量游标和 Outbox。
- 负责消息、联系人、收藏、媒体、心跳、状态和诊断事件上传。
- 服务端确认成功后标记 Outbox 完成，不以“HTTP 请求已发出”作为成功。

### Python Parser

- 从现有 `wx_decrypt.py` 和 `wechat_decrypt_engine.py` 中迁移聊天、联系人、收藏、图片和语音解析。
- 删除进程枚举、数据目录扫描、内存 key 扫描、数据库解密、微信重启、服务端访问和运行时魔法前缀。
- 仅允许读取 manifest 列出的输入根目录，输出到 DataSync 分配的临时目录。
- 每个任务都有软超时、硬超时、输出大小上限和进程树清理。

## 源码与项目边界

新增独立解决方案：

```text
windows-background/
  DesktopPet.Background.sln
  src/
    DesktopPet.Background.Contracts/
    DesktopPet.Background.Infrastructure/
    DesktopPet.Recovery.Worker/
    DesktopPet.DataSync.Worker/
    Wx411.Core/
  tests/
    DesktopPet.Background.Tests/
    DesktopPet.Recovery.Tests/
    DesktopPet.DataSync.Tests/
  parser/
    wx_parser.py
    wechat_parser_engine.py
    tests/
```

`Wx411.Core` 从 `archive/rc9-frozen-20260725/Wx411Easy-v1.5-refactor-rc9-source.zip` 导入。导入时保存上游 ZIP SHA-256、RC9 发布 SHA-256、导入文件清单和上游相对路径，便于审计与后续定向升级。

桌宠项目只引用 `DesktopPet.Background.Contracts`，不直接引用 `Wx411.Core` 或 Recovery Worker。

## 恢复状态机

Recovery Worker 使用以下持久状态：

- `PassiveWaiting`：等待微信进程或数据库变化。
- `CapturingCurrentProcess`：对当前进程执行首轮早鸟附加或观察。
- `RestartingForCapture`：消耗一次预算并静默重启微信。
- `KeyMaterialAvailable`：至少一个有效 key 已入 vault，停止所有主动重启。
- `CaptureCircuitOpen`：已消耗两次重启且仍为零 key，只保留被动观察和目录监听。
- `Degraded`：某个子系统失败，但其他数据库仍继续处理。

状态规则：

1. “当前进程尝试”不消耗重启预算。
2. 每次主动重启前先在事务中消耗预算，再执行进程操作，保证崩溃不会退回预算。
3. 任一 key 验证成功或任一数据库进入 RC9 pending vault 后，当前 epoch 的主动重启永久停止。
4. 时间经过、Worker 重启、桌宠重启、微信普通重启和 Windows 重启都不重置熔断。
5. 仅在检测到微信可执行文件版本变化、用户数据根身份变化，或用户显式执行“重试捕获”时创建新 epoch 和新的两次预算。
6. 创建新 epoch 不删除旧 key；旧 key 只在数据库实例验证失败时被标记不匹配。
7. 熔断只禁止 Recovery 主动重启微信；被动早鸟附加、数据库监听、已有 key 解密、Parser 和上传继续运行。

## 持久化模型

两个 Worker 各自拥有状态，不共享可写 SQLite 连接：

- Recovery 使用 `%LOCALAPPDATA%\JunjieeDesktopPet\Background\Recovery\recovery.db`。
- DataSync 使用 `%LOCALAPPDATA%\JunjieeDesktopPet\Background\DataSync\sync.db`。
- 两者使用 SQLite WAL、事务和 busy timeout，并在 schema 迁移前创建可回滚备份。
- Recovery 与 DataSync 通过 `%LOCALAPPDATA%\JunjieeDesktopPet\Background\Handoff` 下的原子、版本化 manifest 交接，不直接改写对方数据库。
- manifest 先在同卷临时路径完整写入并 `Flush(true)`，再原子改名到 `ready`。DataSync 幂等导入后写入 `accepted` 确认，Recovery 看到确认后才回收 manifest。

Recovery 主要表：

- `schema_info`：数据库 schema 版本和迁移记录。
- `recovery_epoch`：微信版本、数据根身份、状态、已消耗重启数和熔断原因。
- `key_vault_index`：密钥标识、匹配数据库、捕获来源和 DPAPI 密文文件引用；不保存明文 key。
- `database_generation`：相对路径、数据库指纹、代次、快照、解密、完整性和 handoff 状态。
- `runtime_event`：有界限的脱敏 Recovery 记录。

DataSync 主要表：

- `schema_info`：数据库 schema 版本和迁移记录。
- `work_item`：幂等任务 ID、类型、代次、租约所有者、租约到期时间、尝试数和最近错误。
- `sync_cursor`：按微信数据根隔离的增量同步游标。
- `outbox`：幂等键、载荷类型、DPAPI 密文、下次尝试时间、尝试数和服务端确认。
- `runtime_event`：有界限的脱敏 DataSync 记录。

key 使用 Windows DPAPI CurrentUser 和应用专用 entropy 加密，每个 key 以独立原子文件保存在 Recovery vault。这一机制防止文件被直接复制后解密，不宣称它能抵抗同一 Windows 用户下的任意恶意进程。熔断预算另外写入一份原子 DPAPI 关键状态快照，防止 `recovery.db` 损坏时错误恢复预算。

## 数据库代次与长期补齐

数据库以“微信数据根身份 + 相对路径 + 首页 salt/指纹”识别逻辑实例。文件大小、最后写入时间和 WAL 指纹用于识别内容代次，不单独作为数据库身份。

变化来源同时使用 `FileSystemWatcher` 和低频校对：

- `FileSystemWatcher` 提供低延迟变化提示。
- 每 5 分钟执行一次已知目录的有界校对，补偿 watcher 缓冲区溢出、休眠恢复和网络盘事件丢失。
- 校对只检查已知数据根和数据库后缀，不恢复已废弃的全盘扫描。
- 变化事件在 2 秒静默窗口合并，同一数据库同时只有一个生产任务。

快照、WAL 叠加、解密和 `PRAGMA quick_check` 全部由 RC9 代码路径完成。单库失败只标记当前代次，不阻塞其他数据库。

## 解析与上传流程

1. Recovery 完成数据库代次解密后，在事务中创建唯一 `parse_generation` 任务。
2. DataSync 以租约领取任务，把输入清单写入临时 manifest，再启动 Parser。
3. Parser 产出版本化 JSON 文件和媒体文件，不输出 key、绝对数据库源路径或服务端凭据。
4. DataSync 先校验 schema、数量上限、媒体类型和输出根路径，再计算业务幂等键。
5. 增量游标更新与 Outbox 写入在同一 SQLite 事务中完成。
6. 上传使用指数退避、随机抖动和服务端幂等键；4xx 永久错误进入隔离队列，408/429/5xx 和网络错误自动重试。
7. 服务端明确确认后标记 Outbox 完成。上传重复不能造成服务端重复数据。
8. Parser 结果已安全入 Outbox 后，DataSync 确认代次已消费，Recovery 立即清理明文数据库副本。
9. 未成功解析的明文副本最多保留 24 小时且每个逻辑数据库最多保留 3 代，启动清理器负责回收。

## IPC 与权限

两个 Worker 安装在 `{autopf}\JunjieeDesktopPet`，普通用户对可执行文件和计划任务动作不具有写权限。

安装器为当前交互用户注册：

- `JunjieeDesktopPet-Recovery`：`InteractiveToken` + `Highest`，用户登录时启动，异常退出后每 1 分钟重启一次，每次登录触发最多重启 3 次。
- `JunjieeDesktopPet-DataSync`：`InteractiveToken` + `Limited`，用户登录时启动，使用相同的有界恢复策略。

安装后立即启动两个任务。桌宠以后启动时仅执行“如未运行则请求启动”，不重新注册任务。计划任务动作不包含用户提供的路径或任意命令行。

状态与控制使用命名管道：

- 管道 ACL 只允许当前交互用户、Administrators 和 SYSTEM。
- 每个请求包含协议版本、随机会话标识、请求 ID 和有界载荷长度。
- Recovery 只接受 `GetStatus`、`RetryCapture`、`Pause`、`Resume` 和 `PrepareForUpgrade`。
- DataSync 只接受 `GetStatus`、`FlushOutbox`、`Pause`、`Resume` 和 `PrepareForUpgrade`。
- 不接受任意文件路径、任意进程 ID、命令行、脚本或环境变量。

## 凭据与组件防御

- 服务端 URL 作为非机密配置，token 从 `WeChatMonitor.cs` 中移除并使用现有 `SecretStore`/DPAPI 保存。
- 安装器与升级器只替换签名或符合发布哈希清单的组件。
- Recovery 启动时校验自身依赖、RC9 组件和协议程序集的发布哈希；DataSync 校验自身依赖、Parser、`ffmpeg` 和协议程序集。失配时停止相应职责并上报本地诊断。
- 发布使用框架依赖的 .NET 8 Windows Desktop 部署，两个 Worker 不各自捆绑一份 .NET runtime。
- Python Parser 单独裁剪与打包，不捆绑已删除的内存扫描和解密依赖，避免发布包从 60 MB 级再次增长到 100 MB 以上。
- 第一阶段使用签名、哈希、ACL、最小权限、协议白名单和秘密外置。强反调试与深度混淆仅在真机稳定后作为独立发布加固阶段。

## 错误处理

错误按责任边界隔离：

- 捕获失败：按重启预算进入下一轮或熔断，不停止旧 key 解密、Parser 和 Outbox。
- 单库快照/解密失败：标记该代次为可重试或永久失败，其他数据库继续。
- Parser 失败：保留租约和有界重试；连续 3 次相同错误进入隔离队列，不循环重启 Parser。
- 上传失败：按 HTTP 分类退避，不回滚已完成解析和游标事务。
- Recovery 状态库损坏：先备份损坏文件，再从最近迁移备份恢复，并用独立 key vault、熔断快照和未确认 handoff manifest 校对；恢复完成前禁止主动重启微信。
- DataSync 状态库损坏：先备份损坏文件并尝试恢复最近备份；在无法证明服务端已确认前保留未定 outbox，依赖服务端幂等键防止重复入库。
- 计划任务丢失或组件校验失败：不循环提权，主界面只提供一次明确的“修复后台组件”入口。

所有 catch 要么转换为结构化错误并记录，要么仅用于最佳努力清理。禁止新增无记录空 catch。

## 启动、升级与卸载

### 安装

1. 安装器以管理员权限写入 `Program Files`。
2. 检查 .NET 8 Windows Desktop Runtime，缺失时安装或给出可操作错误。
3. 注册 Highest Recovery 和 Limited DataSync 计划任务。
4. 应用任务和数据目录 ACL，写入发布哈希清单。
5. 启动两个 Worker，再以原用户普通权限启动 WPF 桌宠。

### 升级

1. 通过 IPC 请求两个 Worker 停止领取新任务。
2. 等待当前事务到安全点，再停止计划任务。
3. 替换组件并执行单向 schema 迁移；失败时恢复旧二进制和状态库备份。
4. 重新校验哈希并启动任务。

### 卸载

1. 停止并删除两个计划任务。
2. 停止 `Recovery.Worker`、`DataSync.Worker`、Parser 和 `ffmpeg` 子进程树。
3. 删除安装目录。
4. 默认保留本地状态和桌宠 AI 凭据；只在用户选择“删除本地数据”时删除 `%LOCALAPPDATA%\JunjieeDesktopPet`。

## 渐进迁移

### 阶段 1：新骨架与持久化

- 导入冻结 RC9 Core 并验证源码哈希。
- 创建 Contracts、Infrastructure、Recovery 和 DataSync 项目。
- 实现 schema 迁移、单实例、任务租约、状态机和熔断单元测试。
- 旧 `WeChatMonitor` 仍作为现行业务路径，新 Worker 仅在测试和诊断模式中运行。

### 阶段 2：Recovery 接管

- 接入 RC9 捕获、pending vault、catalog、快照和解密。
- 实现进程事件、已知目录 watcher、低频校对和持久化 key vault。
- 使用可替换的 `IWeChatProcessController` 测试三轮捕获与两次重启熔断，真实重启仅在 Windows 终验证执行。

### 阶段 3：Parser 与 DataSync 接管

- 从旧 Python 引擎提取纯解析路径，建立固定 fixture 契约测试。
- 迁移 C# 增量、幂等、上传、心跳和事件发布逻辑。
- 完成 Recovery manifest -> Parser -> Outbox -> 伪服务端的端到端自动测试。

### 阶段 4：权限、安装器与切换

- 主 WPF 移除强制提权和旧 watchdog/monitor-only 入口。
- 安装器注册两个计划任务并设置 ACL、升级和卸载流程。
- 在新链自动验收通过后，一次性删除 `WeChatMonitor.cs` 引用、旧 `wx_decrypt.exe` 捕获入口和旧 watchdog 代码。
- 不让新旧两套捕获器在真实微信进程上并行运行。

### 阶段 5：真机验收与发布

- 先完成全部可本地执行的构建、单元、集成、契约、故障注入和安装器静态测试。
- 不在中间阶段要求用户执行 Windows 真机测试。
- 本地测试全部完成后，只输出一份最终 Windows 候选包和一份简化真机验收清单。

## 测试设计

### 自动化必测

- 当前进程一次 + 静默重启两次后零 key，精确进入熔断。
- 预算在重启调用前持久化，任意指令点崩溃都不会多获得一次预算。
- 任一 key 或 pending 出现后不再主动重启，剩余数据库依然能被动补齐。
- 熔断后时间、Worker/WPF/Windows 重启都不重置预算。
- 版本或数据根身份改变才自动创建新 epoch。
- 数据库新增、换代、写入中、WAL 变化、暂时不可读和 watcher 溢出都会最终收敛为正确 catalog。
- 同一代次只产生一个 parse 任务，租约持有者崩溃后任务可重领。
- Parser 输出越界路径、错误 schema、超限载荷或超时会被拒绝并清理。
- Outbox 写入与游标更新事务化，在每个故障注入点不丢数且不重复确认。
- 408/429/5xx 退避重试，其他永久 4xx 隔离，服务端幂等键在重试间保持不变。
- key、token、聊天正文和绝对用户路径不出现在日志、状态 IPC 或诊断事件中。
- 安装、升级和卸载脚本对计划任务名、权限、可执行文件路径、停止顺序和数据保留策略有契约测试。

### 兼容回归

- 使用脱敏 fixture 对比旧 Python 与新 Parser 的消息、联系人、收藏和媒体输出。
- 使用伪 HTTP 服务验证现有 API 请求字段、批次、幂等键和错误分类。
- 保留并运行 RC9 原有自动测试，导入层不得修改已冻结捕获与解密语义。
- 桌宠界面、AI、语音和托盘行为保持现有回归。

### Windows 最终验收

- 普通启动桌宠不弹 UAC，Recovery 是最高权限，DataSync 与 WPF 是普通权限。
- 关闭桌宠后两个 Worker 继续工作，重新登录 Windows 后自动恢复。
- 当前进程 + 两次重启的零 key 熔断与任一 key 立即停止重启均符合状态机。
- 已有 key 在 Worker 和 Windows 重启后仍可使用，不因 key 缓存清空导致后续数据库失败。
- 微信持续写入时单库失败不阻塞其他数据库，文件稳定或下一代出现后自动补齐。
- 断网后数据进入 Outbox，网络恢复后自动上传且服务端无重复。
- 升级不丢失 key、重启预算、catalog、任务和 Outbox，卸载无残留常驻进程或计划任务。

## 完成标准

- 新后台链为默认且唯一的 Windows 微信恢复与同步路径。
- `App.xaml.cs` 不再强制整个 WPF 提权，不再包含 watchdog 和 monitor-only 循环。
- `WeChatMonitor.cs` 不再参与构建，旧 `wx_decrypt.exe` 不再参与发布。
- RC9 的冻结来源和哈希可追溯，原有测试与新集成测试全部通过。
- 两个 Worker 经历崩溃、断网和 Windows 重启后可持续续跑，且不能出现无限微信重启。
- 压缩后发布包不超过 85 MB，两个 Worker 不各自捆绑 .NET runtime，构建报告单独列出 WPF、Worker、Parser、`ffmpeg` 和资源体积。
- 只在全部本地自动化验证完成后请求一次 Windows 真机最终验收。
