# 微信数据库解密部分成功 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在必需聊天数据库可验证时继续导出和上传聊天记录，并将辅助数据库失败以脱敏、可操作的诊断信息上报。

**Architecture:** Python 解密引擎为每个数据库生成统一结果条目，再由汇总函数决定是否允许消息导出。Windows 客户端继续转发结构化运行事件；本地后端只调整中文展示，区分必需库失败和辅助库降级。

**Tech Stack:** Python 3、`unittest`、`ast` 函数级测试、C# / WPF 运行事件、Flask 服务端展示。

## Global Constraints

- 仅 `message/message_*.db` 和 `session/session.db` 是必需库。
- 必需库失败时绝不导出或上传聊天记录。
- 辅助库失败时允许聊天记录导出和上传，但必须产生脱敏告警。
- 事件不得包含密钥、数据库字节、聊天正文或绝对用户目录。
- 不修改 Android 端；后端只保留本地，不推送远程仓库。

---

### Task 1: 定义数据库结果分类与汇总规则

**Files:**
- Modify: `windows/wechat_decrypt_engine.py:206-337`
- Create: `tests/test_wechat_decrypt_result_summary.py`

**Interfaces:**
- Produces: `is_required_sync_database(rel_path: str) -> bool`
- Produces: `build_decrypt_tree_result(db_results: list[dict], snapshot_result: dict, started_at: float) -> dict`
- Consumes: 每项 `db_results` 的 `db_rel`, `required`, `result`, `reason`, `db_size_bytes`, `wal_applied`, `wal_present` 字段。

- [ ] **Step 1: 写入必需库判定的失败测试**

```python
def test_session_and_message_databases_are_required() -> None:
    required = load_function("is_required_sync_database", {"re": re})

    assert required("message/message_0.db") is True
    assert required("session/session.db") is True
    assert required("contact/contact.db") is False
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `python3 tests/test_wechat_decrypt_result_summary.py`

Expected: FAIL because `is_required_sync_database` does not exist.

- [ ] **Step 3: 实现最小必需库判定**

```python
def is_required_sync_database(rel_path: str) -> bool:
    rel_norm = str(rel_path or "").replace("\\", "/").lower()
    return rel_norm == "session/session.db" or bool(
        re.fullmatch(r"message/message_\\d+\\.db", rel_norm)
    )
```

- [ ] **Step 4: 增加辅助库降级与必需库阻断的失败测试**

```python
def test_optional_failure_allows_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize([
        {"db_rel": "message/message_0.db", "required": True, "result": "success", "reason": ""},
        {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
        {"db_rel": "contact/contact.db", "required": False, "result": "failed", "reason": "integrity_failed"},
    ], {"attempts": 1}, 0.0)

    assert result["success"] is True
    assert result["can_export_messages"] is True
    assert result["optional_failure_count"] == 1


def test_required_failure_blocks_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize([
        {"db_rel": "message/message_0.db", "required": True, "result": "failed", "reason": "key_unmatched"},
        {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
    ], {"attempts": 1}, 0.0)

    assert result["success"] is False
    assert result["can_export_messages"] is False
    assert result["required_failure_count"] == 1
    assert result["failure_reason"] == "key_unmatched"
```

- [ ] **Step 5: 运行测试并确认失败**

Run: `python3 tests/test_wechat_decrypt_result_summary.py`

Expected: FAIL because `build_decrypt_tree_result` does not exist.

- [ ] **Step 6: 实现最小汇总函数**

实现时只汇总传入条目，返回 `success`、`can_export_messages`、成功/失败/跳过计数、`required_failure_count`、`optional_failure_count`、`required_failures`、`optional_failures`、首个 `failure_reason`、快照尝试次数和耗时。失败条目仅保留 `db_rel`、`reason`、`required`。

- [ ] **Step 7: 运行测试确认通过**

Run: `python3 tests/test_wechat_decrypt_result_summary.py`

Expected: PASS.

### Task 2: 将分类结果接入实际解密流程

**Files:**
- Modify: `windows/wechat_decrypt_engine.py:206-337`
- Modify: `tests/test_wechat_decrypt_result_summary.py`

**Interfaces:**
- Consumes: Task 1 的 `is_required_sync_database` 与 `build_decrypt_tree_result`。
- Produces: `client_decrypt_db_result` 的 `required`、规范化 `reason`；`client_decrypt_tree_done` 的 `can_export_messages` 与失败明细。

- [ ] **Step 1: 写入缺失必需库密钥的失败测试**

```python
def test_required_database_without_key_blocks_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize([
        {"db_rel": "message/message_0.db", "required": True, "result": "skipped", "reason": "key_unmatched"},
        {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
    ], {"attempts": 1}, 0.0)

    assert result["can_export_messages"] is False
    assert result["failure_reason"] == "key_unmatched"
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `python3 tests/test_wechat_decrypt_result_summary.py`

Expected: FAIL because skipped required entries are not yet classified as failures.

- [ ] **Step 3: 改造 `decrypt_database_tree`**

对全部导出相关数据库建立结果条目，而不是只遍历 `keys` 中的数据库。没有密钥的库产生 `result="skipped"`、`reason="key_unmatched"` 和 `required`。稳定快照失败产生 `snapshot_unstable`。HMAC 或页面解密失败产生 `decrypt_failed`。SQLite 校验失败产生 `integrity_failed`。每项事件加入 `required`，并把完整条目交给 Task 1 的汇总函数。

- [ ] **Step 4: 用汇总结果控制成功条件**

`decrypt_database_tree` 返回的 `success` 与 `can_export_messages` 只在所有必需库成功时为真；辅助库失败仍返回成功且携带 `optional_failures`。保留既有总数与 WAL 指标，保证旧监控页面兼容。

- [ ] **Step 5: 运行测试确认通过**

Run: `python3 tests/test_wechat_decrypt_result_summary.py && python3 tests/test_wx_decrypt_detection.py`

Expected: 两个脚本均 PASS。

### Task 3: 将解密结果传递到导出与本地监控页

**Files:**
- Modify: `windows/wx_decrypt.py:1448-1657`
- Modify: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/server.py:3638-3652,4729-4750`
- Create: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/test_decrypt_event_display.py`

**Interfaces:**
- Consumes: `decrypt_database_tree` 的 `can_export_messages`、`required_failures`、`optional_failures`。
- Produces: 带 `failure_reason` 与失败明细的 `client_disk_pipeline_result`；中文事件文本。

- [ ] **Step 1: 写入后端展示的失败测试**

```python
def test_tree_event_labels_optional_failures_as_degraded() -> None:
    text = monitor.describe_event("client_decrypt_tree_done", {
        "success_count": 2,
        "failed_count": 1,
        "skipped_count": 0,
        "required_failure_count": 0,
        "optional_failure_count": 1,
        "can_export_messages": True,
        "optional_failures": [{"db_rel": "contact/contact.db", "reason": "integrity_failed"}],
    })

    assert "聊天记录可继续导出" in text
    assert "辅助库失败 1 个" in text
```

- [ ] **Step 2: 运行测试并确认失败**

Run (from `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server`): `python3 -m unittest test_decrypt_event_display -v`

Expected: FAIL because the test file and degraded-status text do not exist.

- [ ] **Step 3: 用 `can_export_messages` 控制导出**

在 `export_v4_messages` 中以 `can_export_messages` 替代笼统的 `success` 判断。必需库失败时，在 `client_disk_pipeline_result` 与 `client_extract_failed` 中附带首个分类原因和 `required_failures`；辅助库失败时继续导出，且 `client_disk_pipeline_result` 带 `optional_failure_count` 与 `optional_failures`。

- [ ] **Step 4: 实现本地后端中文展示**

为 `client_decrypt_db_result` 显示“必需库/辅助库”与分类原因；为 `client_decrypt_tree_done` 显示“聊天记录可继续导出”或“必需库失败，已阻止上传”，以及至多三个相对路径失败项。只读取已有的脱敏字段。

- [ ] **Step 5: 运行完整验证**

Run in the main project: `python3 tests/test_wechat_decrypt_result_summary.py && python3 tests/test_wx_decrypt_detection.py && python3 -m py_compile windows/wx_decrypt.py windows/wechat_decrypt_engine.py /Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/server.py`

Run in `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server`: `python3 -m unittest test_decrypt_event_display -v`

Expected: 全部 PASS，且无语法错误。
