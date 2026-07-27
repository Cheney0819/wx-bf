# Parser Failure Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Make the DataSync parser failure visible at the exact client boundary and preserve the current parser because it already reads the latest real WeChat database schema.

**Architecture:** Keep `DesktopPet.DataSync.Worker` as the job coordinator and `wx_parser.exe` as the bounded child process. Add a stage-aware diagnostic payload to runtime telemetry, sanitize process diagnostics into stable codes, and expose the same metrics to the existing backend timeline. Do not replace the parser reader until a real stage diagnostic proves a schema failure.

**Tech Stack:** C# 12, .NET 8, xUnit, Python standard-library parser, existing MySQL monitor timeline.

## Global Constraints

- Preserve schema-v1 parser job/result contracts.
- Keep keys, tokens, absolute paths, stderr paths, and message content out of telemetry.
- Keep parser, upload, and Recovery responsibilities separated.
- Keep failed parse jobs and handoffs durable for retry.
- Use existing event names `datasync_parser_failed` and `datasync_parser_completed`.

### Task 1: Add a failing stage-diagnostic regression test

**Files:**
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/EndToEndDataSyncTests.cs`
- Test fixture helper: existing `CreateParserArtifactAsync` and `ReadLatestRuntimePayloadAsync`

**Interfaces:**
- Consumes: a parser artifact that exits non-zero and writes a bounded stderr code.
- Produces: an assertion that `datasync_parser_failed` stores `stageKey`, `exitCode`, and sanitized `stderrCode`.

- [ ] **Step 1: Write the failing test**

Add a test that publishes the normal fixture handoff, creates a parser artifact that exits with code `2` and writes `database_path_outside_input_root`, runs `ProcessOneParserJobAsync`, parses the latest local runtime payload, and asserts:

```csharp
Assert.Equal("process_exit", payload.GetProperty("metrics").GetProperty("stageKey").GetString());
Assert.Equal(2, payload.GetProperty("metrics").GetProperty("exitCode").GetInt32());
Assert.Equal(
    "database_path_outside_input_root",
    payload.GetProperty("metrics").GetProperty("stderrCode").GetString());
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter FullyQualifiedName~ParserFailureDiagnostics
```

Expected: FAIL because the current failure event only stores `{"code":"bounded_failure"}`.

### Task 2: Implement stage-aware parser diagnostics

**Files:**
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncRuntime.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/ParserContracts.cs` only if a small diagnostic record is needed
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/EndToEndDataSyncTests.cs`

**Interfaces:**
- Consumes: `ParserProcessResult` with exit code and bounded stderr.
- Produces: runtime and telemetry payloads with `metrics.stageKey`, optional `exitCode`, `stderrCode`, `stdoutTruncated`, and `stderrTruncated`.

- [ ] **Step 1: Track one stage variable around each parser boundary**

Use these stable values in `ProcessOneParserJobAsync`: `input_list`, `job_build`, `process_start`, `process_exit`, `completion_parse`, `result_validate`, `outbox_commit`, and `completed`.

- [ ] **Step 2: Add a sanitized diagnostic serializer**

Extract only the first stderr token matching `[a-z0-9_:-]{1,80}`; otherwise emit `stderr_present`. Never serialize raw stderr or paths.

- [ ] **Step 3: Extend `RecordEventAsync` with optional metrics**

Store the metrics in the local runtime event and pass the same JSON element to `OperationalTelemetryEnvelope`. Existing callers continue to emit an empty object.

- [ ] **Step 4: Emit `datasync_parser_failed` with stage metrics**

Keep the existing bounded failure code and return `true` so the worker continues reconciling later work.

- [ ] **Step 5: Run the focused test and verify GREEN**

Run the focused command from Task 1 and expect one passing test.

### Task 3: Surface stage metrics in the monitor timeline

**Files:**
- Modify: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/server.py`
- Test: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/test_datasync_event_display.py`

**Interfaces:**
- Consumes: existing `payload_json.metrics.stageKey`, `exitCode`, and `stderrCode`.
- Produces: Chinese timeline details such as “解析器在进程退出阶段失败；退出码 2；错误码 database_path_outside_input_root”。

- [ ] **Step 1: Add a failing event-display test**

Post a `datasync_parser_failed` event with stage metrics and assert the rendered title/details include the translated stage and sanitized code.

- [ ] **Step 2: Run the backend focused test and verify RED**

Run:

```bash
python3 -m pytest -q server/test_datasync_event_display.py
```

- [ ] **Step 3: Update the existing event detail formatter**

Keep historical events without metrics readable using the existing generic message.

- [ ] **Step 4: Run all backend display tests and verify GREEN**

Run the same command and expect all tests passing.

### Task 4: Verify current parser and package boundary

**Files:**
- Test/verification only: `windows-parser/tests`, `windows-background/tests/DesktopPet.DataSync.Tests`
- Build script review: `windows-parser/build-parser.ps1`, `windows-pet-wpf/tests/validate-release.ps1`

- [ ] **Step 1: Run parser tests**

```bash
python3 -m pytest -q windows-parser/tests
```

- [ ] **Step 2: Run DataSync tests**

```bash
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj -p:EnableWindowsTargeting=true
```

- [ ] **Step 3: Run release-scope validation and inspect Parser manifest rules**

Confirm the packaged layout contains `Background/Parser/wx_parser.exe` and `Background/Parser/parser-install.json`, and that the manifest hash is generated from the same executable copied into the release tree.

- [ ] **Step 4: Re-query the production monitor after the diagnostic build**

Use the next cloud run to distinguish `process_start`, `process_exit`, `completion_parse`, `result_validate`, and `outbox_commit` before making any reader replacement.

### Task 5: Commit and package the diagnostic build

**Files:**
- Modify: version/build metadata only after tests pass
- Artifact: `发布包/1.0.17/桌宠-1.0.17-安装包.exe`

- [ ] **Step 1: Run the full client test and release build**
- [ ] **Step 2: Verify installer size and SHA-256**
- [ ] **Step 3: Commit implementation and packaging metadata**

