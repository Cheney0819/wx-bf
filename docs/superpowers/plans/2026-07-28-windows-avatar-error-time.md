# Windows Avatar, Error Recovery, and Event Time Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Populate Windows contact/message avatars, stop recovered errors from recurring in heartbeats, and display the original client event time in the monitor.

**Architecture:** Extend the existing parser contact projection and message-resolution pass without changing the parser contract. Derive error clearing from occurrence-ordered telemetry in `TelemetryOutboxWriter`. Parse the existing `payload.occurred_at_utc` field at the monitor ingestion boundary and reuse the current monitor-log timestamps.

**Tech Stack:** Python 3, sqlite3, pytest, C# 12, .NET 8, xUnit, Flask, unittest, JavaScript embedded in Flask HTML.

## Global Constraints

- Windows only; do not modify Android code or behavior.
- Preserve the fixed server-token flow and the existing no-extra-Weixin-restart recovery policy.
- Preserve legacy contact databases that do not expose avatar URL columns.
- Preserve all unrelated uncommitted changes in `C:/Users/20301/Desktop/vx项目/开发项目-桌宠/wechat-monitor`.
- Do not migrate historical monitor rows because the test database will be cleared.
- Every behavior change starts with a test observed failing for the expected reason.

---

### Task 1: Clear Recovered Operational Errors

**Files:**
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/TelemetryHandoffImporterTests.cs`
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/StatusOutboxWriterTests.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryOutboxWriter.cs`

**Interfaces:**
- Consumes: `TelemetryOutboxWriter.CommitAsync(OperationalTelemetryEnvelope, CancellationToken)` and occurrence-ordered `operational_state` writes.
- Produces: `ReadOperationalState` emits `error = ""` for verified recovery events; `StatusOutboxWriter` omits the cleared value.

- [ ] **Step 1: Write failing lifecycle tests**

Add a theory that commits `recovery_capture_failed/capture_attach_failed` followed by either `client_wechat_decrypt_export_result/partial_success` or `datasync_parser_completed/success`, then asserts `error` is empty. Add a heartbeat integration test that performs the same failure/recovery sequence and asserts the decrypted status payload has no `error` property. Finally commit a newer recovery failure and assert it becomes the current error.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj -c Release --filter "FullyQualifiedName~TelemetryHandoffImporterTests|FullyQualifiedName~StatusOutboxWriterTests"
```

Expected: the new recovery assertions fail because `ReadOperationalState` does not clear error for `partial_success` or `datasync_parser_completed`.

- [ ] **Step 3: Implement the minimal state rule**

Extend `ReadOperationalState` with explicit success predicates:

```csharp
if (envelope.EventName == "datasync_parser_completed" &&
    envelope.Code == "success")
{
    state["error"] = "";
}

if (envelope.Component == "recovery" &&
    envelope.EventName == "client_wechat_decrypt_export_result" &&
    envelope.Code is "success" or "partial_success")
{
    state["error"] = "";
}
```

Keep failure writes and the repository's `OccurredAtUtc` ordering unchanged.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass with zero failures.

- [ ] **Step 5: Commit the client change**

```powershell
git add windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryOutboxWriter.cs windows-background/tests/DesktopPet.DataSync.Tests/TelemetryHandoffImporterTests.cs windows-background/tests/DesktopPet.DataSync.Tests/StatusOutboxWriterTests.cs
git commit -m "fix(datasync): clear recovered heartbeat errors"
```

---

### Task 2: Propagate Avatar URLs Through the Windows Parser

**Files:**
- Modify: `windows-parser/tests/test_parser_contract.py`
- Modify: `windows-parser/parser_core.py`

**Interfaces:**
- Consumes: Weixin `contact` columns `username`, `alias`, `remark`, `nick_name`, optional `small_head_url`, optional `big_head_url`.
- Produces: contact `avatar: str` and received-message `avatar: str` in the existing parser JSON contract.

- [ ] **Step 1: Write failing parser tests**

Create temporary contact tables covering: both URL columns with small preferred, empty small with big fallback, legacy four-column schema, direct received message, and group received message whose sender avatar differs from the room avatar. Assert exact URL strings and no parser notice for the legacy schema.

- [ ] **Step 2: Run the avatar tests and verify RED**

Run:

```powershell
python -m pytest windows-parser/tests/test_parser_contract.py -q -k "avatar or legacy_contact_schema"
```

Expected: URL assertions fail with `avatar == ""`; the legacy test continues to pass.

- [ ] **Step 3: Implement schema-aware contact projection**

Add focused helpers:

```python
def _contact_avatar_columns(connection: sqlite3.Connection) -> tuple[bool, bool]:
    columns = {str(row[1]).casefold() for row in connection.execute("PRAGMA table_info(contact)")}
    return "small_head_url" in columns, "big_head_url" in columns


def _avatar_url(row: sqlite3.Row) -> str:
    keys = set(row.keys())
    small = _text(row["small_head_url"]).strip() if "small_head_url" in keys else ""
    big = _text(row["big_head_url"]).strip() if "big_head_url" in keys else ""
    return small or big
```

Build each contact SELECT from the detected columns, use `_avatar_url` in both contact readers, and set each received message avatar from `sender_target` for groups or `chat_username` for direct chats after contact lookup.

- [ ] **Step 4: Run parser tests and verify GREEN**

```powershell
python -m pytest windows-parser/tests/test_parser_contract.py -q -k "avatar or legacy_contact_schema"
python -m pytest windows-parser/tests -q
```

Expected: focused tests and all parser tests pass.

- [ ] **Step 5: Commit the parser change**

```powershell
git add windows-parser/parser_core.py windows-parser/tests/test_parser_contract.py
git commit -m "fix(parser): export contact avatar URLs"
```

---

### Task 3: Render URL and Legacy Base64 Avatars

**Files:**
- Modify: `C:/Users/20301/Desktop/vx项目/开发项目-桌宠/wechat-monitor/server/test_datasync_event_display.py`
- Modify: `C:/Users/20301/Desktop/vx项目/开发项目-桌宠/wechat-monitor/server/server.py`

**Interfaces:**
- Consumes: non-empty avatar strings returned by the existing contacts/messages APIs.
- Produces: image markup for HTTP(S) URLs or Base64 data, with initial-letter replacement on image load failure.

- [ ] **Step 1: Write failing HTML contract tests**

Assert the rendered `HTML_TEMPLATE` contains one helper that selects URL versus `data:image/jpeg;base64,`, uses escaped values, and provides an image `error` handler that replaces both library and chat avatars with the existing fallback markup.

- [ ] **Step 2: Run the monitor display test and verify RED**

```powershell
python -m pytest server/test_datasync_event_display.py -q -k avatar
```

Run from the monitor repository. Expected: helper/URL/fallback assertions fail because both views always prefix Base64.

- [ ] **Step 3: Implement shared avatar source and fallback helpers**

Add JavaScript helpers equivalent to:

```javascript
function avatarImageSource(value) {
    const avatar = String(value || '').trim();
    return /^https?:\/\//i.test(avatar) ? avatar : `data:image/jpeg;base64,${avatar}`;
}
```

Generate escaped image sources and replace failed images with `.library-avatar-fallback` or `.chat-avatar.other` markup. Use the helpers from `renderLibraryAvatar` and `renderChatMessages`.

- [ ] **Step 4: Run display tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass.

---

### Task 4: Preserve Client Event Occurrence Time

**Files:**
- Modify: `C:/Users/20301/Desktop/vx项目/开发项目-桌宠/wechat-monitor/server/test_datasync_event_display.py`
- Modify: `C:/Users/20301/Desktop/vx项目/开发项目-桌宠/wechat-monitor/server/server.py`

**Interfaces:**
- Consumes: `payload.occurred_at_utc` ISO-8601 string from `TelemetryOutboxWriter.SerializePayload`.
- Produces: timezone-naive UTC `datetime` passed as `created_at` to `log_event`; server receipt time fallback for invalid input.

- [ ] **Step 1: Write failing ingestion tests**

Post a delayed event with `occurred_at_utc: "2026-07-28T03:59:15+00:00"`, mock `log_event`, and assert its `created_at` argument is `datetime(2026, 7, 28, 3, 59, 15)`. Add invalid and offset-free inputs and assert they pass no client-derived time.

- [ ] **Step 2: Run occurrence-time tests and verify RED**

```powershell
python -m pytest server/test_datasync_event_display.py -q -k occurred_at
```

Expected: the valid-time assertion fails because `collect_event` does not pass `created_at`.

- [ ] **Step 3: Implement bounded ISO-8601 parsing**

Add a helper that accepts strings, handles a trailing `Z`, requires `tzinfo`, converts to UTC and drops `tzinfo`. Reject values more than five minutes ahead of `datetime.now(timezone.utc)`. Call it once in `collect_event` and pass the result to `log_event` only through the existing `created_at` parameter.

- [ ] **Step 4: Run monitor tests and verify GREEN**

```powershell
python -m pytest server/test_datasync_event_display.py -q
```

Expected: all monitor display tests pass.

- [ ] **Step 5: Preserve monitor ownership boundary**

Review only these paths:

```powershell
git diff -- server/server.py server/test_datasync_event_display.py
```

Do not stage or commit the dirty monitor repository. The user deploys this repository separately.

---

### Task 5: Full Verification, Build, and True-Device Retest

**Files:**
- Verify: all client and monitor paths changed above.
- Modify only if required by the established release process: existing Windows version/build metadata.

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: tested Windows client commit, installer, and true-device evidence.

- [ ] **Step 1: Run complete automated suites**

```powershell
python -m pytest windows-parser/tests -q
dotnet test windows-background/DesktopPet.Background.sln -c Release --no-restore
python -m pytest server -q
```

Run the first two commands from the client repository and the last command from the monitor repository. Record exact pass/fail counts.

- [ ] **Step 2: Build the Windows target and installer**

Use the repository's existing `build.ps1` and release/version conventions. Verify the build reports zero errors and locate the produced installer and SHA-256.

- [ ] **Step 3: Install and run the true-device scenario**

Keep the current Weixin process running. Install the new build, start only the desktop pet, and allow all capture, parser, upload, and heartbeat work to run automatically. Do not manipulate Android, parser jobs, queues, or Weixin restarts manually.

- [ ] **Step 4: Verify true-device evidence**

Inspect `%LOCALAPPDATA%/JunjieeDesktopPet/Background` and the monitor UI/logs. Confirm avatars appear for URL-bearing contacts and chat messages, old errors no longer recur after recovery, delayed events show the client occurrence time, parser and upload events succeed, Weixin PID stays constant, and automatic restart count is zero.

- [ ] **Step 5: Final client commit and push**

```powershell
git status --short
git log --oneline origin/main..main
git push origin main
```

Push only after all required verification is green. Confirm the new Windows GitHub Actions build completes successfully and report its run and artifact.
