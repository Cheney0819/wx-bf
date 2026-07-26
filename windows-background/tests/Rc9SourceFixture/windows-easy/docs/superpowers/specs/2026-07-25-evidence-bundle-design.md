# 一键验收证据包设计

## 目标

用户完成普通恢复、精准捕获或取消后，只需点击一次“导出证据包”，即可得到可审查、可校验的 ZIP。程序自动记录可验证事实；缺失证据标记为 `INCOMPLETE`，失败标记为 `FAIL`，不推断或伪造 `PASS`。

## 架构

功能拆成三个高内聚单元：

1. `EvidenceSessionRecorder` 维护当前应用会话的结构化操作记录。它记录操作类型、时间、进程选择、源文件前后快照、输出文件检查、诊断 JSON、结果和目录快照。
2. `EvidenceFileInspector` 负责流式 SHA-256、目录枚举和 SQLite `integrity_check`。SQLite 检查在系统临时目录中的数据库副本上运行，避免给源文件或输出文件创建 WAL/SHM。
3. `EvidenceBundleService` 只负责渲染摘要、序列化 JSON、脱敏文本、复制诊断 JSON、生成包内 SHA-256，并把 ZIP 原子写入输出目录。

`MainForm` 只编排 UI：开始操作前调用记录器，成功、失败或取消后结束记录；导出按钮把记录器快照和窗口日志交给打包服务。扫描、捕获、解密、DPAPI 和原子数据库输出逻辑保持不变。

## 操作记录

操作类型：

- `OrdinaryRecovery`
- `CompatibilityCheck`
- `PreciseCapture`

结果：

- `Running`
- `Success`
- `Failed`
- `Cancelled`

每条记录包含：

- 操作 ID、开始/结束 UTC
- 所选 PID、进程名称、是否自动扫描
- 输出目录
- 参与的源数据库路径、大小、操作前/后 SHA-256 或明确错误
- 输出数据库路径、大小、SHA-256、`integrity_check` 结果
- 诊断 JSON 路径及是否成功打包
- 操作前/后的输出目录清单
- 失败类型和消息，不含堆栈及敏感缓冲

应用环境包含工具版本、OS 描述、64 位状态和证据会话开始时间。机器名、用户名不采集。

## ZIP 格式

最终文件名：`Wx411Easy-evidence-<UTC时间>-<短ID>.zip`

固定条目：

- `SUMMARY.txt`
- `evidence.json`
- `window-log.txt`
- `SHA256SUMS.txt`

可选条目：

- `diagnostics/<原文件名>.json`

ZIP 不包含 `.db`、`.sqlite`、`.capture`、内存转储、key 或 DPAPI 密文。源和输出数据库只记录路径、长度、哈希与检查结果。

## 门禁摘要

- A：存在成功的 `OrdinaryRecovery`，至少一个输出通过哈希和完整性检查，且诊断 JSON 可用。
- B：存在成功的 `PreciseCapture`，至少一个输出通过哈希和完整性检查。
- C：存在取消的 `PreciseCapture`，取消后的目录无 `.tmp`、`-wal`、`-shm`、`.tmp-wal`、`.tmp-shm`；DPAPI 票据复用证据缺失时保持 `INCOMPLETE`。
- D：所有被记录操作的源文件操作前后 SHA-256 均存在且一致。

总结果只有 A-D 均为 `PASS` 时才为 `PASS`；任一 `FAIL` 时为 `FAIL`；否则为 `INCOMPLETE`。

## 原子性与错误处理

- ZIP 先写到同目录唯一 `.tmp` 文件，关闭并验证条目后再 `File.Move` 到最终路径。
- 失败时精确删除本次临时 ZIP 和系统临时检查目录，不清理用户文件。
- 文件在检查期间消失、被占用或无权限时，记录错误并继续生成证据包。
- 导出进行时禁用操作按钮；当前恢复/捕获进行时禁用导出按钮，避免会话快照竞态。
- 同名诊断 JSON 使用稳定后缀消除冲突。

## 脱敏

- 不读取或导出 DPAPI `.capture` 内容。
- 日志和诊断文本对带 `key`、`raw key`、`key hex` 标签的 64/96 位十六进制值做替换。
- 哈希、salt fingerprint、PID、模块 SHA-256 和数据库路径保留，因为它们是验收证据。
- 结构化模型不存在原始 key 字段。

## 测试

- 文件哈希、缺失文件、目录排序和临时边车识别。
- SQLite 正常、损坏、检查取消，并验证原文件目录未产生边车。
- 会话的成功、失败、取消状态及源文件前后快照。
- A-D 和总结果判定。
- ZIP 固定/可选条目、包内 SHA-256、诊断同名冲突、数据库和 `.capture` 排除。
- 日志十六进制 key 脱敏。
- 打包失败后的临时文件清理。
- `MainForm` 按钮、操作接线和 busy 状态源代码契约。

## 非目标

- 不自动启动、登录或控制目标应用。
- 不自动执行 A-D。
- 不把应用自身完整性检查描述成外部独立检查。
- 不改变扫描、捕获、密码学、DPAPI 或数据库输出算法。
