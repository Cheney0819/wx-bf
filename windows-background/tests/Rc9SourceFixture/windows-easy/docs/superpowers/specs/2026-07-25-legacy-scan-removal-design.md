# RC8 单一精准捕获版设计

## 目标

RC8 只保留“定位 key 并解密”这一条恢复路径。旧的内存候选扫描和“兼容检查 30 秒”从界面、生产代码、测试、说明和验收语义中完整移除；RC7 产物保持原样。

## 方案选择

考虑过三种方案：仅隐藏旧按钮、保留旧服务但断开调用；删除旧 UI 与服务但保留扫描器；完整删除旧链路并迁移精准捕获仍需的进程发现。RC8 选择第三种，因为它才能避免失效代码继续占位，同时用独立的 `TargetProcessDiscovery` 保住精准捕获的自动进程发现。

## 界面与流程

- “重新扫描”改名为“刷新列表”，其职责仍只是刷新目标进程和数据库列表。
- 删除“一键恢复并生成副本”和“兼容检查 30 秒”按钮、事件与 `StartRecoveryAsync(bool monitorMode)`。
- “定位 key 并解密”成为唯一主按钮，也是窗体默认确认按钮。
- 标题、副标题、提示和错误信息只描述精准捕获、列表刷新和 5 分钟早鸟等待，不再宣传 72-profile 内存扫描或 30 秒兼容检查。
- 保留取消、打开输出目录、导出证据包、选择数据库和选择输出目录。

## 代码边界

- `CallpointCaptureRecoveryService` 使用 `TargetProcessDiscovery.Discover()`，不再依赖 `ProcessMemoryScanner.FindProcesses()` 或 `ProcessInfo`。
- 删除旧扫描专用的恢复服务、内存遍历、key 文本/内存候选扫描、AES schedule 扫描、codec holder 扫描和旧诊断报告代码。
- `RecoveryContracts` 保留精准流程使用的 `RecoveryProcessSelection`、`RecoveryProgress`、`CaptureRecoveryResult`；删除旧流程独占的 `RecoveryResult` 与 `DiagnosticRecoveryException`。
- 精准捕获的 SQLCipher profile 探测、全页认证、WAL 处理、DPAPI pending 票据、原子输出和进程文件句柄优先级保持不变。

## 证据门禁

- `EvidenceOperationKind` 只保留 `PreciseCapture`。
- 删除 `EvidenceOperationRecord.Diagnostics`、记录器的 `diagnosticPaths` 参数和证据 ZIP 的 `diagnostics/` 收集通道；精准流程不再产生旧诊断 JSON。
- Gate A 状态为 `NotApplicable`，摘要显示 `N/A`，说明旧扫描已在 RC8 废弃，不再要求 `OrdinaryRecovery`。
- Gate B 继续验证精准捕获成功、输出 SHA-256 与 `integrity_check=ok`。
- Gate C 继续验证取消后目录清洁，以及后续成功运行确认 DPAPI pending 票据未复用。
- Gate D 继续报告源数据库前后 SHA-256 是否变化，但只作为观察项。目标程序在监听期间可能正常改写源库，因此 Gate D 不参与总结果。
- 总结果只由 Gate B 与 Gate C 决定：任一 `FAIL` 则 `FAIL`；二者都 `PASS` 则 `PASS`；其余为 `INCOMPLETE`。

## 测试与发布

- 先增加 RC8 合同测试，确认旧 UI 文案、旧入口、旧操作类型和旧源文件尚存在时失败。
- 迁移进程发现并用现有精准捕获合同测试验证自动发现、早鸟等待、PID 刷新和辅助 PID 跳过行为。
- 刷新发现的 PID 只有在已加载 `Weixin.dll` 后才进入已调度集合；尚未加载的 PID 在全局 5 秒、每 300 ms 一次且可取消的宽限期内继续发现，不按 PID 累加等待。
- 删除仅覆盖旧扫描实现的测试；保留密码学、数据库认证、WAL、精准捕获、DPAPI 与证据包测试。
- 全量运行 Release 测试和 `dotnet format --verify-no-changes`，再发布 Windows x64 自包含单文件。
- 新建 `dist/refactor-rc8/`，包含 RC8 EXE、中文 `TEST-STEPS.txt`、ZIP 与 `SHA256SUMS.txt`；校验 ZIP 内容、PE x64 GUI 身份、哈希和 RC7 原哈希。

## 明确不做

- RC8 不实现 `media_0` 的持久化后台续跑。
- 不修改精准捕获的 180 秒监听、5 分钟进程早鸟等待或 4 个观察点。
- 不改写或覆盖 RC1-RC7 的任何发布文件。
