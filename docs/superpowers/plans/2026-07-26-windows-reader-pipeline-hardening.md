# Windows Reader Pipeline Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair the Windows database recovery, parser, durable outbox, and server reader path without changing Android or adding media backfill.

**Architecture:** Keep the existing Recovery -> atomic handoff -> DataSync Parser -> encrypted Outbox -> server flow. Tighten each boundary with version-compatible normalization, generation-aware state, content-versioned identities, fail-closed authentication, and database-enforced request ordering.

**Tech Stack:** C# 12/.NET 8/xUnit/Microsoft.Data.Sqlite, Python 3/pytest/sqlite3/zstandard, Flask/SQLAlchemy/MySQL-compatible SQL.

## Global Constraints

- Android is out of scope.
- Media backfill is out of scope.
- Never expose database keys, plaintext database paths, or message bodies in diagnostics.
- Preserve schema-v1 compatibility; optional cursor fields may be added, but existing documents remain accepted.
- Every production change starts with a focused failing regression test.
- Do not overwrite unrelated changes in `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor`.

---

### Task 1: Repair parser publication, path normalization, compression, and backfill

**Files:**
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncRuntime.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/ParserContracts.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/ParserJobBuilder.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/ParserResultValidator.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/IncrementalOutboxWriter.cs`
- Modify: `windows-parser/parser_contract.py`
- Modify: `windows-parser/parser_core.py`
- Modify: `windows-parser/requirements-build.txt`
- Test: `windows-parser/tests/test_parser_contract.py`
- Test: `windows-parser/tests/test_parser_fixture_equivalence.py`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ParserJobBuilderTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ParserResultValidatorTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/IncrementalOutboxWriterTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/EndToEndDataSyncTests.cs`

**Interfaces:**
- `ResolveParserInstallPath(baseDirectory)` resolves `Background/DataSync` to sibling `Background/Parser/parser-install.json` and preserves a local-development fallback.
- Parser schema 1 accepts optional `cursor` and returns optional `nextCursor`; absence means first/only page.
- A cursor is a deterministic opaque JSON/base64 value containing the stable `(create_time, table_name, local_id)` boundary per message table plus contact/favorite row boundaries.

- [ ] **Step 1: Write failing regressions**

Add tests proving the packaged sibling path is selected, `db_storage/message/message_0.db` yields the same messages as `message/message_0.db`, flag 4 Zstd bytes decode to the original UTF-8 text, 5,001 records produce a second page, and a changed contact/favorite payload creates a new identity while an unchanged payload remains deduplicated.

- [ ] **Step 2: Verify RED**

Run:

```bash
python3 -m pytest -q windows-parser/tests
$HOME/.dotnet/dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj -p:EnableWindowsTargeting=true --filter "ParserJobBuilderTests|ParserResultValidatorTests|IncrementalOutboxWriterTests|EndToEndDataSyncTests"
```

Expected: the new path, Zstd, pagination, and content-version identity tests fail.

- [ ] **Step 3: Implement the smallest compatible changes**

Normalize only the leading historical `db_storage/` segment before database classification. Add `zstandard` to the packaged parser and decompress flag 4 with an output bound. Implement keyset pagination without OFFSET, carry `nextCursor` through the C# contracts, and enqueue a continuation parse job until `nextCursor` is absent. Hash all mutable contact/favorite fields in `ParserItemIdentity`.

- [ ] **Step 4: Verify GREEN**

Run both commands from Step 2 plus:

```bash
python3 -m pytest -q windows-parser/tests
$HOME/.dotnet/dotnet test windows-background/DesktopPet.Background.sln -p:EnableWindowsTargeting=true --nologo --verbosity minimal
```

Expected: all parser and DataSync tests pass.

### Task 2: Repair Recovery completeness, WAL generation, account binding, and capture termination

**Files:**
- Modify: `windows-background/src/Wx411.Core/DatabaseProbeCatalog.cs`
- Modify: `windows-background/src/Wx411.Core/CallpointProfile.cs`
- Modify: `windows-background/src/Wx411.Core/Windows/BreakpointRestorer.cs`
- Modify: `windows-background/src/Wx411.Core/Windows/DebugCaptureBackend.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/PersistedKeyDecryptor.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/RecoveryCoordinator.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/RecoveryStateMachine.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/WeChatDataRootLocator.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/WeChatIdentityProvider.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/Rc9CaptureAdapter.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/Security/ValidatedKeyVault.cs`
- Test: corresponding files under `windows-background/tests/Wx411.Core.Tests` and `windows-background/tests/DesktopPet.Recovery.Tests`

**Interfaces:**
- `DatabaseFileGeneration` includes a stable WAL fingerprint; a WAL-only append changes equality and generation ID.
- `PersistedDecryptResult` exposes unresolved required databases; `RecoveryCoordinator` calls live capture whenever that set is non-empty.
- `WeChatRuntimeIdentity` carries PID/session/executable identity into root resolution; ambiguity yields `ambiguous_data_root`.
- Breakpoint restoration receives the worker cancellation token and a finite deadline.

- [ ] **Step 1: Write failing regressions**

Cover partial persisted-key success followed by live capture, WAL-only generation change, a malformed vault record followed by a valid key, INI redirected roots, two-account ambiguity, pending tickets from another epoch, unsupported module classification without restart-budget use, and restoration timeout while the target remains alive.

- [ ] **Step 2: Verify RED**

Run:

```bash
$HOME/.dotnet/dotnet test windows-background/tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -p:EnableWindowsTargeting=true --filter "DatabaseProbeCatalogTests|BreakpointRestorerTests|CallpointCaptureRecoveryServiceTests"
$HOME/.dotnet/dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj -p:EnableWindowsTargeting=true --filter "PersistedKeyDecryptorTests|RecoveryCoordinatorTests|RecoveryStateMachineTests|WeChatDataRootLocatorTests|Rc9CaptureAdapterTests"
```

Expected: every newly added regression fails for its identified root cause.

- [ ] **Step 3: Implement generation and completeness fixes**

Hash the stable WAL prefix in the generation model. Continue per-key after corrupt vault entries. Return explicit resolved/unresolved sets and suppress capture only when required databases are complete. Scope pending records by root/epoch/database identity.

- [ ] **Step 4: Implement root/profile/restoration guards**

Use the active process and supported INI configuration to bind one data root; reject ambiguity. Return `unsupported_module` before starting a capture round or consuming a restart. Bound breakpoint restore by deadline and propagate cancellation through `DebugCaptureBackend`; on terminal restore failure exit through the existing controlled relaunch path.

- [ ] **Step 5: Verify GREEN**

Run the two focused commands and then the full solution command from Task 1. Expected: all Recovery and Core tests pass without hanging.

### Task 3: Repair Outbox credentials, quarantine recovery, and server request semantics

**Files:**
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncRepository.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Upload/OutboxUploader.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Upload/ServerSettingsVault.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/OutboxUploaderTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncRepositoryTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ServerSettingsVaultTests.cs`
- Modify carefully: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/server.py`
- Modify carefully: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/mysql_schema.sql`
- Create or modify: `/Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server/test_reader_pipeline_security.py`

**Interfaces:**
- `RequeueQuarantinedOutboxAsync(statusCodes, cancellationToken)` moves only matching quarantined rows to pending and preserves ciphertext/idempotency keys.
- All DataSync API routes call one fail-closed constant-time token validator.
- Receipt uniqueness is enforced by `(endpoint, request_id)` before business effects.
- Heartbeat updates apply only when incoming `sequence` is greater than the stored value.

- [ ] **Step 1: Write failing client regressions**

Prove source code has no fixed production token, missing settings yield `credential_missing`, 401/403 rows can be requeued after settings change, and unrelated quarantined 4xx rows stay isolated.

- [ ] **Step 2: Write failing server regressions**

Prove every reader GET returns 401 without/with wrong token, an empty configured token never authenticates, concurrent duplicate request IDs create one business effect, and heartbeat sequence 10 cannot be overwritten by sequence 9.

- [ ] **Step 3: Verify RED**

Run:

```bash
$HOME/.dotnet/dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj -p:EnableWindowsTargeting=true --filter "OutboxUploaderTests|DataSyncRepositoryTests|ServerSettingsVaultTests"
python3 -m pytest -q /Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server
```

Expected: the new authentication, requeue, receipt, and sequence tests fail.

- [ ] **Step 4: Implement client and server fixes**

Remove the fallback token and rely on the existing protected settings bootstrap. Requeue only authentication quarantines after a verified credential update. Centralize route authentication. Reserve receipts atomically with a unique constraint and commit business changes with the receipt in one transaction. Use a conditional heartbeat upsert keyed by sequence.

- [ ] **Step 5: Verify GREEN**

Run both commands from Step 3. Expected: all DataSync and server tests pass.

### Task 4: Release-layout and end-to-end verification

**Files:**
- Modify only if a regression requires it: `build.ps1`
- Add: `windows-background/tests/DesktopPet.DataSync.Tests/PublishedParserLayoutTests.cs`

- [ ] **Step 1: Build Parser and stage the exact Background layout**

Run the existing `windows-parser/build-parser.ps1`, publish DataSync, and stage `Background/DataSync` beside `Background/Parser` exactly as `build.ps1` does.

- [ ] **Step 2: Execute a handoff-to-parser smoke test**

Use the readable-set fixture with a `db_storage/message/...` handoff, run the packaged executable, validate schema, counts, hashes, and the Zstd message text.

- [ ] **Step 3: Run full regression suites**

```bash
python3 -m pytest -q windows-parser/tests
python3 -m pytest -q /Users/jiee/Desktop/开发项目-桌宠/wechat-monitor/server
$HOME/.dotnet/dotnet test windows-background/DesktopPet.Background.sln -p:EnableWindowsTargeting=true --nologo --verbosity minimal
git diff --check
```

Expected: every suite passes and no whitespace errors are reported.

- [ ] **Step 4: Final review**

Review the complete diff for path containment, secret leakage, cancellation propagation, migration compatibility, unrelated server changes, and Android exclusions. Record remaining risk that Windows-only debugger behavior and the installer executable still need final Windows-machine acceptance.
