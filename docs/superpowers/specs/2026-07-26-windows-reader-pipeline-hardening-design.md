# Windows 读取器链路硬化设计

## 目标

修复 Windows 端从微信数据库取数、解密、Parser、Outbox 到服务端读取接口的确定性故障，确保发布目录中的 Parser 能启动、Recovery 产物能被读取、增量变化不会被旧代次吞掉、历史数据可持续补齐，且上传与读取接口不会因空凭据、乱序心跳或并发回执产生错误结果。Android 和媒体补传不在本次范围。

## 范围与成功标准

- P0：发布后 Parser 路径可启动；`db_storage/...` 与 `message/...` 的交接路径一致；服务端所有数据读取 GET 需要有效 token。
- P1：部分数据库成功时未解析库仍继续捕获；WAL-only 变化产生新代次；Zstd 正文正确解压；历史数据使用有界分页/游标补齐；联系人和收藏字段更新会再次入队；4xx quarantine 可在凭据修正后恢复；不支持的微信模块版本快速失败；断点恢复有硬超时；客户端不再内嵌固定上传 token，服务端拒绝空 token。
- P2（与用户确认的账号错配/请求竞态相关）：数据根必须与当前微信进程身份绑定，配置重定向可发现；回执创建具备原子幂等；心跳序列只允许单调推进；pending ticket 按 epoch/root 隔离。
- 不上传密钥、绝对源路径或聊天正文到诊断事件；保留现有协议版本和向后兼容字段，新增字段可选。

## 设计

### 1. 发布与 Parser 交接

Recovery 只发布受控的相对路径。DataSync Worker 解析器路径由 `AppContext.BaseDirectory/../Parser` 和同级发布布局解析，并在启动前校验 `parser-install.json`。Parser 入口接受 `message/` 与 `db_storage/message/` 两种历史 manifest 形式，将其归一到受控输入根下的同一相对路径，拒绝绝对路径和目录穿越。解析结果保持 schema 1。

Parser 在读取消息字段时检查压缩标志：flag 4 使用现有 Zstandard 解码器，其他 flag 保持现有 UTF-8/转义逻辑；解码失败按单条记录隔离并记录计数。消息、联系人和收藏读取改为游标分页，每页大小有上限，游标包含稳定主键与更新时间，直到结果集耗尽；旧调用方的 `limit=5000` 只作为页大小而不是总上限。

### 2. Recovery 与账号绑定

数据库 generation 指纹包含 `.db`、稳定 `.db-wal` 前缀及 `.db-shm` 元信息。命中已完成 output 时，只有源 generation 与 WAL 指纹完全相同才允许复用；WAL 变化必须重新快照、解密和 quick-check。

解密汇总按数据库逐项返回 `resolved`、`pending`、`failed`。只有全部必需库 resolved 才抑制主动捕获；partial 结果仍对 unresolved 库运行 live capture。坏 key 文件按单 key 隔离，后续有效 key 继续尝试。

数据根选择优先使用当前微信进程的 PID、命令行/句柄和配置 INI 映射；若多个候选无法唯一绑定则返回 `ambiguous_data_root`，停止自动解密，不选择“最近修改”的目录。pending ticket 记录 root identity、epoch、database salt/module，只参与同一上下文的状态判断。

调用点 profile 预检支持明确的 `unsupported_module` 结果，不消耗重启预算。断点恢复使用取消令牌和硬截止时间；超时后执行受控 detach/进程重启，确保 Worker 能返回持久化状态。

### 3. Outbox 与服务端

客户端从 DPAPI/SecretStore 读取 token，缺失时阻止上传并报告 `credential_missing`，不使用源码默认值。服务端启动配置缺少 token 时拒绝启动；认证比较使用常量时间比较，所有数据 GET 与上传路由统一走认证依赖。

Outbox 对可恢复的认证错误提供 requeue/reset 操作：quarantine 行保留原 payload、错误和幂等键，凭据更新后按租约批量重新入队。联系人/收藏幂等身份包含内容 hash 或 `updated_at`，同一主键的字段更新不会被永久去重。

服务端以数据库唯一约束原子创建 receipt；重复 request id 返回已保存结果且不重复执行业务写入。心跳更新带 `sequence`，仅当新序列大于当前序列时覆盖，旧序列返回已接受但不回退状态。

## 错误处理与兼容

- 单条解析失败、辅助库失败、单个坏 key 不阻断其他可用数据。
- 必需库未 resolved 时禁止消息上传，且任务保留可重试状态。
- 所有新错误写入现有 runtime event/diagnostic contract，不暴露密钥和正文。
- 现有服务端客户端字段继续接受；认证缺失变成明确的 401/503，而非静默成功。

## 测试策略

先写失败回归测试，再实现：

1. 发布布局与 Parser handoff 的真实目录 smoke test。
2. `db_storage/message`、WAL-only、Zstd、分页和字段更新 fixture tests。
3. partial key、坏 key、账号歧义、INI 重定向、profile unsupported、断点超时 recovery tests。
4. 未认证 GET、空 token、quarantine requeue、并发 receipt、乱序 heartbeat tests。
5. 运行现有 Parser、server 和 .NET 全套测试；最后在临时发布目录启动 `wx_parser.exe` 并核验结果文件与 SHA。

## 非目标

媒体二次上传、Android 端、旧 WPF 界面重构和强反调试加固另行处理。
