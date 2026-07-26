# DataSync Telemetry And Backend Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the existing dashboard's operational value while making client identity, business uploads, status heartbeats, and events durable and idempotent through DataSync.

**Architecture:** DataSync imports or creates one stable client identity and freezes it into every encrypted Outbox payload. Recovery publishes only sanitized atomic telemetry files; DataSync validates and atomically converts them to local events plus encrypted Outbox rows. The Flask/MySQL backend caches successful responses in transactional request receipts and maps both legacy and new event names into the existing dashboard model.

**Tech Stack:** C# 12, .NET 8, SQLite, DPAPI, Microsoft.Extensions.Hosting, Python 3, Flask, PyMySQL, pytest, xUnit.

## Global Constraints

- Work only in `.worktrees/recovery-worker-foundation`; do not touch Android.
- Recovery owns processes, restarts, keys, decryption, and readable generations; it has no server URL, token, or HTTP client.
- DataSync owns client identity, server settings, telemetry import, encrypted Outbox, status, and upload.
- Parser receives no identity, telemetry, token, key, process, or network capability.
- Preserve all existing GET response shapes and single-user access behavior.
- Requests without `request_id` keep legacy behavior.
- WPF startup, `WeChatMonitor.cs`, `wx_decrypt.py`, installer behavior, and real cutover remain unchanged.
- Use TDD for every production behavior and keep each task in a separate commit.

---

### Task 1: Persist client identity and freeze it into business Outbox payloads

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/Identity/ClientIdentity.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Identity/ClientIdentityStore.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/IncrementalOutboxWriter.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ClientIdentityStoreTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/IncrementalOutboxWriterTests.cs`

**Interfaces:**
- Produces: `ClientIdentityDocument(int SchemaVersion, string SessionId, string Source, DateTimeOffset CreatedAtUtc)`.
- Produces: `IClientIdentityProvider.GetAsync(CancellationToken)` and atomic `ClientIdentityStore`.
- Changes: `IncrementalOutboxWriter` requires a `ClientIdentityDocument` and serializes `request_id`, `session_id`, `source`, then the endpoint array.

- [x] **Step 1: Write failing identity and payload tests**

Cover legacy `{ "session_id": "client-cs-existing" }` import with source `client_cs`, new `client-datasync-<guid>` generation, reopen stability, corrupt/oversized rejection, and decrypted business payload identity fields.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "ClientIdentityStoreTests|IncrementalOutboxWriterTests"`

Expected: FAIL because identity types and identity-bearing writer constructor do not exist.

- [x] **Step 3: Implement the identity boundary**

Use schema 1 JSON, `AtomicFile.ReplaceAsync`, a 64 KiB limit, strict JSON members, whitespace-normalized 120-character session IDs, and `[a-z0-9_-]{1,32}` sources. Read the legacy file only during first creation and never mutate it. Pass `<AppContext.BaseDirectory>/wechat_data/client_identity.json` from production Worker composition.

- [x] **Step 4: Verify GREEN and commit**

Run the focused test command, then commit:

```text
feat: persist datasync client identity
```

### Task 2: Make backend request IDs transactionally idempotent

**Files:**
- Create: `server/request_receipts.py`
- Create: `server/tests/test_request_receipts.py`
- Modify: `server/server.py`

**Interfaces:**
- Produces: `normalize_request_id(value) -> str`, `request_key(request_id) -> str`.
- Produces: `claim_request(conn, endpoint, session_id, client_source, request_id) -> RequestClaim`.
- Produces: `complete_request(conn, claim, response: dict) -> None`.
- `RequestClaim` contains `is_new`, `request_key`, and optional `cached_response`.

- [x] **Step 1: Write failing receipt tests**

Use a deterministic fake PyMySQL connection/cursor to prove request validation, SHA-256 keys, first-claim execution, cached retry response, missing-ID legacy behavior, and rollback-visible SQL ordering. Add route contract assertions that all five POST routes call claim before their first domain mutation and complete before commit.

- [x] **Step 2: Verify RED**

Run: `python3 -m pytest -q server/tests/test_request_receipts.py`

Expected: FAIL because `server.request_receipts` and receipt schema do not exist.

- [x] **Step 3: Implement receipt storage and route integration**

Add `request_receipts` with endpoint/session/source/request key uniqueness and cached `response_json`. On MySQL duplicate key code 1062, select the completed row `FOR UPDATE` and return the cached response without domain/log/session writes. Integrate messages, contacts, favorites, status, and events. Preserve requests without IDs. Delete receipts and raw `event_logs` older than 30 days during initialization.

- [x] **Step 4: Verify GREEN and commit**

Run the focused backend tests and `python3 -m py_compile server/server.py server/request_receipts.py`, then commit:

```text
feat: honor backend request identities
```

### Task 3: Publish sanitized Recovery telemetry through atomic handoff files

**Files:**
- Create: `windows-background/src/DesktopPet.Background.Contracts/OperationalTelemetryContracts.cs`
- Create: `windows-background/src/DesktopPet.Background.Infrastructure/AtomicTelemetryPublisher.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/RecoveryCoordinator.cs`
- Modify: `windows-background/src/DesktopPet.Recovery.Worker/Program.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/AtomicTelemetryPublisherTests.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/RecoveryCoordinatorTests.cs`

**Interfaces:**
- Produces: `OperationalTelemetryEnvelope` schema 1 with 64-hex event ID, component, event name, severity, code, UTC timestamp, and bounded `JsonElement` metrics.
- Produces: `IOperationalTelemetryPublisher.PublishAsync(OperationalTelemetryDraft, CancellationToken)`.
- Produces: atomic `<eventId>.json` files under `Handoff/Telemetry/ready`.

- [x] **Step 1: Write failing publisher and coordinator tests**

Prove atomic filename identity, strict sanitized fields, concurrent unique publication, and coordinator emission for capture start/result, restart start/result, handoff publication, and coordinator failure. Assert telemetry publisher failure never changes Recovery action or restart accounting.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter "AtomicTelemetryPublisherTests|RecoveryCoordinatorTests"`

Expected: FAIL because telemetry contracts and publisher injection do not exist.

- [x] **Step 3: Implement low-frequency Recovery events**

Use only stable codes, booleans, counts, durations, and version values. Reject path-like or secret-shaped metric names and strings. Wrap publisher calls in a best-effort helper that records only `telemetry_publish_failed` locally and never masks primary Recovery results.

- [x] **Step 4: Verify GREEN and commit**

Run the focused Recovery tests, then commit:

```text
feat: publish sanitized recovery telemetry
```

### Task 4: Import telemetry atomically into local diagnostics and encrypted Outbox

**Files:**
- Modify: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncSchema.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncModels.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncRepository.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryEnvelopeValidator.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryOutboxWriter.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryHandoffImporter.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/TelemetryHandoffImporterTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncSchemaMigrationTests.cs`

**Interfaces:**
- Upgrades DataSync schema 1 to schema 2 with `imported_telemetry(event_id PRIMARY KEY, ...)` and `operational_state(key PRIMARY KEY, value_json, updated_at_utc)`.
- Produces: `TelemetryHandoffImporter.ImportAsync(path, cancellationToken) -> TelemetryImportResult`.
- Produces: one atomic transaction for telemetry identity, local runtime event, operational state, and encrypted `events` Outbox insertion.

- [x] **Step 1: Write failing migration and importer tests**

Prove schema-1 upgrade without data loss, strict 64 KiB validation, filename/event ID match, unknown-member rejection, identity-bearing encrypted event payload, idempotent crash replay, invalid-file rejection, and continued processing of later valid files.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "TelemetryHandoffImporterTests|DataSyncSchemaMigrationTests"`

Expected: FAIL because schema 2 and telemetry importer types do not exist.

- [x] **Step 3: Implement schema migration and telemetry commit**

Event request IDs are SHA-256 of `desktop-pet-datasync-event-v1|<session>|<eventId>`. Delete a valid source file only after commit. Move invalid files atomically to `Handoff/Telemetry/rejected` without copying payload text into runtime diagnostics.

- [x] **Step 4: Verify GREEN and commit**

Run focused tests, then commit:

```text
feat: import telemetry into encrypted outbox
```

### Task 5: Add durable status heartbeat, DataSync outcomes, and dashboard mapping

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/Telemetry/StatusOutboxWriter.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Upload/UploadModels.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Upload/OutboxUploader.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncRuntime.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncWorker.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Modify: `server/server.py`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/StatusOutboxWriterTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncWorkerTests.cs`
- Test: `server/tests/test_monitor_event_mapping.py`

**Interfaces:**
- Adds `DataSyncWorkerOptions.HeartbeatInterval`, fixed at 60 seconds in production.
- Adds `IDataSyncRuntime.EnqueueHeartbeatAsync` and `ReconcileTelemetryAsync`.
- Extends `UploadResult` with endpoint identity so business upload outcomes can emit events without decrypting payloads twice.

- [x] **Step 1: Write failing heartbeat, recursion, and mapping tests**

Prove immediate and 60-second heartbeats, replacement of old unleased pending status, preservation of leased status, identity fields, missing-settings durability, business-only upload outcome events, no event/status recursion, and new backend stage/title mappings.

- [x] **Step 2: Verify RED**

Run:

```text
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "StatusOutboxWriterTests|DataSyncWorkerTests|OutboxUploaderTests"
python3 -m pytest -q server/tests/test_monitor_event_mapping.py
```

Expected: FAIL at the missing heartbeat/runtime/mapping interfaces.

- [x] **Step 3: Implement heartbeat and event integration**

Reconcile both telemetry and database manifests at startup, on hints, and every five minutes. Enqueue heartbeat before the initial upload poll and every 60 seconds. Emit DataSync state-changing events after core state commits; telemetry write failure records a bounded local code and does not reverse core work. Add `client_datasync` label and mappings to existing monitor-log stage keys.

- [x] **Step 4: Verify GREEN and commit**

Run focused .NET and Python tests, then commit:

```text
feat: deliver durable worker telemetry
```

### Task 6: Close the cross-contract E2E and report

**Files:**
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/EndToEndDataSyncTests.cs`
- Create: `server/tests/test_api_contract.py`
- Create: `docs/superpowers/reports/2026-07-26-datasync-telemetry-backend-contract.md`
- Modify: `docs/superpowers/plans/2026-07-26-datasync-telemetry-backend-contract.md`

**Interfaces:**
- Proves legacy identity import, business payload context, Recovery telemetry import, status coalescing, response-loss retry, and one logical backend effect per request ID.

- [x] **Step 1: Extend the E2E and backend contract tests**

Assert messages/contacts/favorites/events/status all carry one stable identity. Simulate backend commit followed by lost response, reopen DataSync, retry the same request, and assert cached success with one logical event. Preserve `500, 429, 200` transient coverage.

- [x] **Step 2: Run all final gates**

```text
python3 -m pytest -q server/tests
python3 -m pytest -q windows-parser/tests
python3 -m pytest -q tests
dotnet test windows-background/DesktopPet.Background.sln
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
python3 -m py_compile server/server.py server/request_receipts.py
git diff --check
```

Expected: all suites pass, Release has zero warnings/errors, and diff gate is clean. Record the missing local MySQL executable as a Windows/server smoke-test item rather than claiming it ran.

- [x] **Step 3: Audit boundaries and write the report**

Confirm no server/token code in Recovery, no identity/network code in Parser, no WPF/installer/Android changes, no event recursion, no unbounded offline status queue, and no unexplained empty production catches.

- [x] **Step 4: Commit**

```text
test: close telemetry backend contract
```

## Self-Review

- Spec coverage: identity, transactional receipts, Recovery event handoff, DataSync atomic import, status coalescing, backend mapping, retention, and E2E each map to a task.
- Boundary coverage: Recovery publishes files only; DataSync alone owns identity, credentials, encryption, and HTTP; Parser remains pure.
- Type consistency: `sessionId/session_id`, `source`, `eventId/event_id`, and `requestId/request_id` retain one meaning across C#, JSON, SQLite, and MySQL.
- Failure coverage: response loss, crash replay, invalid telemetry, missing settings, telemetry publication failure, and event-recursion prevention are explicit tests.
- Environment truth: MySQL locking receives a real deployment smoke test later because this macOS host has neither MySQL nor Docker; local tests use deterministic fake transaction objects and never claim server integration execution.
