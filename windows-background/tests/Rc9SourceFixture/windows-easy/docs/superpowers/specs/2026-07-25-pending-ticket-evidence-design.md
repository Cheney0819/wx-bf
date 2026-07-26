# Pending Ticket Evidence Design

## 目标

修复门禁 C 永远无法由真实 UI 操作达到 `PASS` 的缺陷。用户在同一次程序启动中完成“精准捕获取消”，随后再次执行精准捕获并成功时，程序自动判断取消运行产生的 DPAPI pending 票据是否在后续运行中被加载复用。

## 范围

- 只补结构化证据记录，不自动启动恢复、捕获或取消操作。
- 只关联同一次程序启动中的操作。
- 不读取、导出或记录票据密文、捕获 payload、key、salt 或 DPAPI 明文。
- RC3 保持不变；修复发布为 RC4。

## 判定规则

每次精准捕获开始前，获取 vault 内票据 ID 集合 `before`。

精准捕获取消后，再获取集合 `after`：

- 两次快照均成功：该取消操作产生的票据集合为 `after - before`。
- 任一快照失败：该取消操作的票据集合未知，后续保持 `INCOMPLETE`。

同一会话中后续精准捕获成功后，捕获服务返回本次从 vault 实际加载的旧票据 ID 集合 `loaded`。证据记录器更新此前尚未确认的取消操作：

- 取消票据集合已知，且与 `loaded` 无交集：`PendingCaptureTicketNotReused = true`。
- 取消票据集合已知，且与 `loaded` 有交集：`PendingCaptureTicketNotReused = false`。
- 取消票据集合未知：保持 `null`。

取消运行没有产生票据时，已知集合为空；后续成功即可形成“未复用”证明。

## 组件边界

### PendingCaptureVault

新增只读元数据 API，枚举当前全部 `.capture` 文件的合法十六进制记录 ID。API 不打开、不解密票据正文。目录不可读或枚举失败时抛出可诊断异常，由 UI 证据辅助逻辑捕获。

### CallpointCaptureRecoveryService

加载 vault 票据时记录票据 ID。成功返回的 `CaptureRecoveryResult` 新增只读集合 `LoadedPendingCaptureTicketIds`。只报告本次从 vault 加载的记录，不报告本次实时捕获后新保存的记录。

### EvidenceSessionRecorder

内部取消状态保存 `CreatedPendingCaptureTicketIds`，该集合不进入导出 JSON。新增两个职责明确的 API：

- 为已完成的精准捕获取消记录写入“本次新产生票据 ID 集合”或未知状态。
- 在后续精准捕获成功后，使用 `LoadedPendingCaptureTicketIds` 更新此前取消记录的布尔证明。

更新在私有锁内完成；导出快照仍只暴露 `PendingCaptureTicketNotReused`。

### MainForm

只负责顺序编排：

1. 精准捕获前获取 vault ID 快照。
2. 取消后获取第二次快照并记录差集。
3. 精准捕获成功且证据操作完成后，提交服务返回的 loaded ID 集合。

证据辅助步骤失败只写窗口日志，不改变捕获本身的成功、失败或取消结果。

## 错误处理

- vault 快照失败：取消记录保持票据证明未知，Gate C 为 `INCOMPLETE`。
- 后续捕获失败或取消：不更新旧取消记录。
- 后续捕获成功但证据记录器更新失败：记录日志，主操作仍成功。
- ID 比较使用不区分大小写的十六进制字符串集合。
- 不把“后续运行成功”本身当作未复用证明，必须完成精确集合比较。

## 测试

- vault ID 快照只返回合法 `.capture` ID，不解密 payload。
- 捕获结果只返回加载的旧票据 ID，不混入本次新保存 ID。
- 取消未产生票据，后续成功后 Gate C 为 `PASS`。
- 取消产生票据但后续未加载，Gate C 为 `PASS`。
- 取消产生票据且后续加载同一 ID，Gate C 为 `FAIL`。
- vault 快照失败时 Gate C 保持 `INCOMPLETE`。
- MainForm 源代码契约覆盖开始快照、取消差集、成功确认顺序。
- 全量 Release 测试、warnings-as-errors、格式检查、win-x64 单文件发布与 ZIP 校验。

## 验收流程

1. 启动 RC4。
2. 点击“定位 key 并解密”，进入监听后取消。
3. 不关闭 RC4，再次点击“定位 key 并解密”并成功生成输出。
4. 点击“导出证据包”。
5. `SUMMARY.txt` 中 Gate C 应为 `PASS`；若后续加载了取消运行产生的票据，则应为 `FAIL`。
