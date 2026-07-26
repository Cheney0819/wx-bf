# 旧聊天读取核心适配 DataSync 设计

## 目标

让常驻 DataSync 链路确认并稳定使用能读取新版微信明文数据库的解析逻辑，解决当前 `wx_parser.exe` 解析阶段失败的问题，同时保留现有后台续跑、增量上传、断点恢复和网页状态协议。

## 已确认事实

- Recovery 使用 `Wx411.Core` 负责密钥捕获和数据库解密。
- 当前 DataSync 通过 `wx_parser.exe` 读取解密后的 SQLite 文件。
- 旧读取核心位于 `windows/wechat_decrypt_engine.py`，其 `export_chatlog_json` 已在真实的“本地数据输出 7”数据库上成功读取 100 条消息，并识别 47 个头像。
- 当前 `windows-parser/wx_parser.py` 源码同样已在这组真实数据库上成功读取 714 条消息和 2314 个联系人；因此现阶段证据指向 Parser 的 Windows 启动、安装清单校验或结果交接，而不是微信字段不兼容。
- 线上失败事件发生在 `datasync_parser_failed`，目前没有 `datasync_parser_completed` 或上传确认。

## 方案

保留当前 `wx_parser.exe` 作为受控子进程和现有 schema-v1 job/result 协议，不替换已通过真实数据库验证的读取实现。先增加 Parser 启动、退出、结果校验和提交阶段诊断；只有诊断确认字段兼容问题后，才将旧读取规则作为适配层按需接入。读取层不负责微信进程发现、密钥、解密、重启、网络或上传。

数据流：

```text
Recovery/Wx411.Core
  -> readable database generations
  -> DataSync job manifest
  -> wx_parser.exe
  -> wx_parser current reader
  -> schema-v1 result.json
  -> encrypted outbox
  -> existing messages/contacts/favorites uploaders
```

## 适配边界

- 输入继续使用 DataSync 创建的受控目录和 manifest，不读取原始微信目录。
- 保留当前读取器的联系人、消息、收藏和文本内容规范化规则，并以旧读取器作为新版结构回退参考。
- 媒体处理与文本读取分离：首轮结果先提交文本、联系人、收藏；图片和语音沿用旧规则在后续补齐，不阻塞首轮上传。
- 单个数据库或单张消息表读取异常记录为 notice 并继续处理；所有候选库均失败时才返回 Parser 失败。
- 输出字段转换到现有 `ParserResultDocument`，不修改服务器接口和 Outbox 身份规则。

## 诊断

Parser 失败事件增加脱敏阶段码：`input_list`、`job_build`、`process_start`、`process_exit`、`completion_parse`、`result_validate`、`outbox_commit`。保留进程退出码和受限 stderr 错误码，不记录密钥、token、绝对路径或聊天内容。

## 验证

1. 使用“本地数据输出 7”真实数据库运行当前 Parser 源码，记录消息和联系人数量。
2. 先写失败测试，再为 Parser 每个边界阶段增加诊断事件，覆盖非零退出、无效 stdout、结果校验和 Outbox 提交。
3. 运行 Parser 契约测试和 DataSync 端到端测试，确认解析完成、Outbox 入队、重启后不重复上传。
4. 重新打包后在云电脑观察网页依次出现具体失败阶段或“解析完成”和上传确认；`messages` 至少出现一批记录。

## 回滚

Parser 安装清单保留上一版 hash；适配器失败时不删除 handoff 或 parse job，恢复上一版 Parser 包即可继续处理未完成队列。
