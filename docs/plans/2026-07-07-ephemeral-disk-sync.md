# Ephemeral Disk Sync Implementation Plan

> **For Claude:** Use `${SUPERPOWERS_SKILLS_ROOT}/skills/collaboration/executing-plans/SKILL.md` to implement this plan task-by-task.

**Goal:** 把微信 4.x 链路切换为“临时落盘解密 -> 读取增量消息 -> 上传 -> 立即清理明文”，避免纯内存链路不稳定，同时减少重复上传。

**Architecture:** `wx_decrypt.exe` 负责识别目录、取密钥、落盘解密并生成 `chatlog_export.json`；`WeChatMonitor.cs` 负责读取导出结果、基于本地同步状态筛选增量消息、推送到服务器，并在每轮结束后删除明文目录和导出文件。服务端继续使用现有消息指纹唯一键做最终去重兜底。

**Tech Stack:** Python 3.11、C# / WPF、System.Text.Json、MySQL、GitHub Actions Windows 打包工作流

---

### Task 1: 固化方案文档

**Files:**
- Create: `docs/plans/2026-07-07-ephemeral-disk-sync.md`

**Step 1: 记录链路切换目标**

写明不再把纯内存 SQLite 作为主链路，而是回到已验证可用的临时落盘链路。

**Step 2: 记录重复上传控制策略**

写明“服务端去重继续保留，客户端新增本地同步状态，只上传未上传过的消息指纹”。

**Step 3: 记录明文清理要求**

写明“每轮开始前先清理残留，每轮结束后无论成功失败都清理”。

### Task 2: 切换 `wx_decrypt.py` 到临时落盘主链路

**Files:**
- Modify: `windows/wx_decrypt.py`

**Step 1: 去掉运行时优先走内存直传的主分支**

保留现有代码作为后备实验能力，但默认让 v4 链路执行：

```python
decrypt_database_tree(...)
export_chatlog_json(...)
write_meta(...)
```

**Step 2: 新增临时落盘事件**

新增事件，用于监控页展示：

- `client_disk_pipeline_started`
- `client_disk_pipeline_result`

**Step 3: 让事件明确声明“本轮会在上传后清理明文”**

在 payload 中带：

```python
{
    "mode": "ephemeral_disk_v4",
    "will_cleanup_after_upload": True
}
```

### Task 3: 给桌宠增加本地增量同步状态

**Files:**
- Modify: `windows/WeChatMonitor.cs`

**Step 1: 新增同步状态文件**

在桌宠私有目录下维护：

`wechat_data/sync_state.json`

内容至少包含：

```json
{
  "recent_message_keys": ["..."],
  "updated_at": "2026-07-07T00:00:00Z"
}
```

**Step 2: 为消息生成稳定指纹**

基于下列字段拼接后做 SHA256：

- `wxid`
- `create_time`
- `is_sender`
- `sender`
- `content`
- `msg_type`
- `msg_sub_type`
- `media_type`
- `media_name`

**Step 3: 提取消息后先做增量过滤**

逻辑：

- 读取 `chatlog_export.json`
- 计算每条消息指纹
- 已存在于 `recent_message_keys` 的消息跳过
- 仅把未出现过的消息发给服务器

**Step 4: 推送成功后刷新本地同步状态**

将本轮成功发送的消息指纹写回本地状态文件，并限制最大保留数量，避免文件无限增长。

### Task 4: 给桌宠增加明文清理

**Files:**
- Modify: `windows/WeChatMonitor.cs`

**Step 1: 每轮开始前清理上轮残留**

删除：

- `wechat_data/decrypted/chatlog_export.json`
- `wechat_data/decrypted/decrypt_meta.json`
- `wechat_data/decrypted/contact/`
- `wechat_data/decrypted/message/`
- `wechat_data/decrypted/session/`
- `wechat_data/decrypted/Contact/`

**Step 2: 每轮结束后再次清理**

无论推送成功、失败、无新消息，都在 `finally` 里执行清理。

**Step 3: 为清理补监控事件**

新增：

- `client_disk_cleanup_started`
- `client_disk_cleanup_result`
- `client_incremental_filter_result`

### Task 5: 更新监控页中文文案

**Files:**
- Modify: `server/server.py`
- Modify: `../server/server.py`

**Step 1: 新增事件标题**

为新增的桌宠事件补中文标题。

**Step 2: 新增事件说明**

让网页能直接读懂：

- 本轮是否走临时落盘
- 本轮识别到多少消息
- 过滤掉多少已同步消息
- 清理是否成功

### Task 6: 本地校验与构建

**Files:**
- Modify: `windows/wx_decrypt.py`
- Modify: `windows/WeChatMonitor.cs`
- Modify: `server/server.py`

**Step 1: 跑 Python 语法检查**

Run:

```bash
python3 -m py_compile windows/wx_decrypt.py server/server.py
```

Expected: PASS

**Step 2: 检查 Git diff**

Run:

```bash
git diff --check
```

Expected: PASS

**Step 3: 提交并推送**

Run:

```bash
git add docs/plans/2026-07-07-ephemeral-disk-sync.md windows/wx_decrypt.py windows/WeChatMonitor.cs server/server.py
git commit -m "Use ephemeral disk sync for Weixin uploads"
git push origin HEAD:main
```

**Step 4: 触发 Windows 构建**

Run:

```bash
gh workflow run build-windows.yml --repo Cheney0819/wx-weflow --ref main
gh run watch <run_id> --repo Cheney0819/wx-weflow --interval 10
```

Expected: 构建成功，产物包含新的临时落盘同步逻辑
