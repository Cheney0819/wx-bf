# DataSync And Pure Parser Worker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consume real Recovery schema-v1 handoffs, parse messages/contacts/favorites without key or process access, persist encrypted upload work, and prove manifest-to-fake-server delivery before any installer or WPF cutover.

**Architecture:** A normal-privilege `DesktopPet.DataSync.Worker` owns handoff import, a durable database catalog, parser leases, incremental item identities, encrypted Outbox rows, HTTP delivery, and status diagnostics. A reduced Python `wx_parser` receives only a DataSync-created job manifest and a controlled input tree assembled from immutable Recovery generations; it contains no process enumeration, memory scanning, database decryption, restart, server, or token code. Recovery remains the sole owner of keys and readable database generation production.

**Tech Stack:** C# 12, .NET 8, Microsoft.Data.Sqlite 8.0.8, Microsoft.Extensions.Hosting 8.0.1, xUnit 2.9.2, Python 3 standard library parser, PyInstaller 6.x for the final parser artifact.

## Global Constraints

- Ignore `android-pet` and do not stage its files.
- Keep `App.xaml.cs`, `WeChatMonitor.cs`, `wx_decrypt.py`, installer projects, and current runtime behavior unchanged in this phase.
- Recovery and DataSync use separate SQLite databases and never write each other's state.
- DataSync never reads Recovery key vaults, pending tickets, source encrypted databases, or process memory.
- The parser may read only paths listed in its versioned job manifest and located below its controlled job input root.
- Server URL/token exist only in the normal-privilege DataSync boundary, protected with current-user DPAPI at rest.
- Every upload begins as a committed encrypted Outbox row; an HTTP attempt is never the source of truth.
- Messages, contacts, and favorites use stable per-item idempotency identities with a unique SQLite constraint.
- Parser soft timeout is 120 seconds; hard timeout is 180 seconds; the full parser process tree is terminated at the hard timeout.
- Parser stdout is one schema-v1 JSON result; stderr is diagnostic text capped at 64 KiB.
- A parser result is accepted only after schema, count, size, path, and source-set validation.
- Installer cutover starts only after a real Recovery handoff fixture passes the end-to-end fake-server gate.
- Keep the combined future Windows distribution ZIP at or below 85 MB; do not publish self-contained .NET runtimes in this phase.
- Use TDD and one task-scoped commit per task.

---

## File Map

```text
windows-background/
  src/
    DesktopPet.DataSync/
      Persistence/DataSyncSchema.cs
      Persistence/DataSyncRepository.cs
      HandoffManifestImporter.cs
      ParserJobBuilder.cs
      ParserProcessSupervisor.cs
      ParserResultValidator.cs
      IncrementalOutboxWriter.cs
      Security/EncryptedOutboxProtector.cs
      Upload/OutboxUploader.cs
      Upload/ServerSettingsVault.cs
    DesktopPet.DataSync.Worker/
      Program.cs
      DataSyncWorker.cs
      HandoffReadyWatcher.cs
      DataSyncDiagnosticReader.cs
      SingleInstanceGuard.cs
      worker.manifest
  tests/
    DesktopPet.DataSync.Tests/
      fixtures/handoff-v1/
      fixtures/parser-input/
      fixtures/parser-output/
windows-parser/
  wx_parser.py
  parser_core.py
  parser_contract.py
  requirements-build.txt
  tests/
    test_parser_contract.py
    test_parser_fixture_equivalence.py
```

## Fixed Contracts

Recovery input remains the existing contract:

```csharp
public sealed record DatabaseReadyManifest(
    int SchemaVersion,
    string ManifestId,
    string EpochId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<DatabaseReadyItem> Databases);

public sealed record DatabaseReadyItem(
    string GenerationId,
    string RelativePath,
    string PlaintextPath,
    string Sha256);
```

The parser job and result contracts introduced in this plan are:

```csharp
public sealed record ParserJobManifest(
    int SchemaVersion,
    string JobId,
    string SourceSetId,
    string InputRoot,
    string OutputRoot,
    IReadOnlyList<ParserDatabaseInput> Databases,
    int MaximumMessages);

public sealed record ParserDatabaseInput(
    string GenerationId,
    string RelativePath,
    string Path,
    string Sha256);

public sealed record ParserResultDocument(
    int SchemaVersion,
    string JobId,
    string SourceSetId,
    IReadOnlyList<ParsedMessage> Messages,
    IReadOnlyList<ParsedContact> Contacts,
    IReadOnlyList<ParsedFavorite> Favorites,
    IReadOnlyList<ParserNotice> Notices);
```

Limits are fixed at 5,000 messages, 5,000 contacts, 1,000 favorites, 1,000 notices, 32 MiB JSON output, and 5 MiB per embedded media payload. This foundation exports image metadata but no external image bytes; controlled media-generation handoff is a separate pre-cutover phase. Voice bytes already stored inside readable database records may be emitted within the 5 MiB limit.

### Task 1: Add DataSync persistence and encrypted row primitives

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/DesktopPet.DataSync.csproj`
- Create: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncSchema.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncModels.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Persistence/DataSyncRepository.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Security/IOutboxProtector.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Security/EncryptedOutboxProtector.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncRepositoryTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/EncryptedOutboxProtectorTests.cs`

**Interfaces:**
- Consumes: `BackgroundPaths.SyncDatabase`, current-user DPAPI through `ISecretProtector`.
- Produces: `IDataSyncRepository`, `ImportedManifest`, `DatabaseCatalogEntry`, `ParseJob`, `OutboxRecord`, and encrypted payload storage.

- [x] **Step 1: Write failing schema and encryption tests**

```csharp
[Fact]
public async Task LeaseAndOutboxSurviveRepositoryReopen()
{
    await using var first = await OpenAsync();
    await first.EnqueueParseJobAsync(Job("job-1"), default);
    var claimed = await first.TryClaimParseJobAsync("worker-a", TimeSpan.FromMinutes(3), default);
    Assert.Equal("job-1", claimed!.Id);
    await first.EnqueueOutboxAsync(Outbox("message:item-1", "messages", "{\"messages\":[]}"u8), default);
    await first.DisposeAsync();

    await using var reopened = await OpenAsync();
    Assert.NotNull(await reopened.GetParseJobAsync("job-1", default));
    Assert.Single(await reopened.GetPendingOutboxAsync(10, default));
}

[Fact]
public void ProtectedOutboxNeverContainsPlaintextTokenOrPayload()
{
    var protector = new EncryptedOutboxProtector(new XorTestProtector());
    var ciphertext = protector.Protect("outbox-1", "messages", "secret-message"u8);
    Assert.Equal(-1, ciphertext.AsSpan().IndexOf("secret-message"u8));
    Assert.Equal("secret-message"u8.ToArray(), protector.Unprotect("outbox-1", "messages", ciphertext));
}
```

- [x] **Step 2: Run tests and verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "DataSyncRepositoryTests|EncryptedOutboxProtectorTests"`  
Expected: FAIL because the DataSync projects and types do not exist.

- [x] **Step 3: Implement schema version 1**

Create `schema_info`, `handoff_manifest`, `database_catalog`, `parse_job`, `parse_job_input`, `exported_item`, `outbox`, and `runtime_event` in one immediate transaction. Use WAL, `synchronous=FULL`, a 5-second busy timeout, foreign keys, and these unique constraints:

```sql
UNIQUE(handoff_manifest.manifest_id)
UNIQUE(database_catalog.generation_id)
UNIQUE(parse_job.source_set_id)
UNIQUE(exported_item.item_type, exported_item.idempotency_key)
UNIQUE(outbox.idempotency_key)
```

`TryClaimParseJobAsync` and `TryClaimOutboxAsync` each use one conditional `UPDATE ... WHERE state='pending' OR lease_until_utc < $now` and return the claimed row only when one row changed. Outbox ciphertext is a BLOB and plaintext payload is never written to SQLite or diagnostics.

- [x] **Step 4: Pass focused tests and commit**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "DataSyncRepositoryTests|EncryptedOutboxProtectorTests"`  
Expected: PASS.  
Commit: `feat: add durable datasync state and encrypted outbox`

### Task 2: Import and acknowledge schema-v1 Recovery handoffs

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/HandoffManifestImporter.cs`
- Create: `windows-background/src/DesktopPet.DataSync/HandoffAcceptancePublisher.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/HandoffManifestImporterTests.cs`
- Test fixture: `windows-background/tests/DesktopPet.DataSync.Tests/fixtures/handoff-v1/ready.json`

**Interfaces:**
- Consumes: `DatabaseReadyManifest` schema 1 and immutable generation files below `BackgroundPaths.RecoveryGenerations`.
- Produces: committed manifest/catalog rows, one parse job per distinct latest-generation source set, and `Handoff/accepted/manifest-id.json`.

- [x] **Step 1: Write failing strict-import tests**

Cover valid import, duplicate import, partial crash before acceptance, SHA mismatch, manifest-ID mismatch, duplicate relative path, unknown schema, JSON above 1 MiB, external plaintext path, Windows traversal, UNC path, missing generation, and cancellation before the accepted rename.

```csharp
[Fact]
public async Task CommitPrecedesAcceptedMarkerAndReimportIsIdempotent()
{
    var first = await _importer.ImportAsync(_readyManifest, default);
    var second = await _importer.ImportAsync(_readyManifest, default);
    Assert.Equal(first.SourceSetId, second.SourceSetId);
    Assert.Single(await _repository.ListManifestsAsync(default));
    Assert.Single(Directory.EnumerateFiles(_acceptedRoot, "*.json"));
}
```

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter HandoffManifestImporterTests`  
Expected: FAIL because the importer does not exist.

- [x] **Step 3: Implement validation and catalog merge**

Recompute every file SHA-256 and the existing Recovery formulas for `generationId` and `manifestId`. Normalize relative paths to `/`, reject rooted/traversal paths on every host OS, and require each file to remain below `RecoveryGenerations`. In one transaction insert the manifest, upsert catalog generations, select the newest accepted generation for every relative path, compute `sourceSetId = sha256(sorted(relativePath:generationId))`, and enqueue a parse job only when that source set is new. After commit, atomically write a schema-v1 accepted marker containing only `schemaVersion`, `manifestId`, `sourceSetId`, and `acceptedAtUtc`.

- [x] **Step 4: Pass tests and commit**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter HandoffManifestImporterTests`  
Expected: PASS.  
Commit: `feat: import recovery handoffs idempotently`

### Task 3: Extract a pure, versioned Python parser

**Files:**
- Create: `windows-parser/parser_contract.py`
- Create: `windows-parser/parser_core.py`
- Create: `windows-parser/wx_parser.py`
- Create: `windows-parser/tests/test_parser_contract.py`
- Create: `windows-parser/tests/test_parser_fixture_equivalence.py`
- Create: `windows-parser/requirements-build.txt`
- Create fixture: `windows-parser/tests/fixtures/readable-set/**`

**Interfaces:**
- Consumes: a schema-v1 parser job manifest and readable SQLite files arranged below its controlled input root.
- Produces: one atomic `ParserResultDocument` schema 1 file, one compact schema-v1 completion object on stdout, and no network/process/key side effects.

- [x] **Step 1: Freeze readable SQLite fixture outputs from current pure readers**

Use existing readable SQLCipher test databases plus small purpose-built SQLite message/contact/favorite fixtures. Record expected normalized JSON under `windows-parser/tests/fixtures/expected/result-v1.json`. Tests compare semantic JSON objects, not formatting or property order.

- [x] **Step 2: Write failing parser contract tests**

```python
def test_parser_rejects_input_outside_controlled_root(tmp_path):
    job = job_manifest(tmp_path, database_path=tmp_path.parent / "outside.sqlite")
    result = run_parser(job)
    assert result.returncode == 2
    assert result.stdout == ""

def test_parser_source_contains_no_forbidden_runtime_ownership():
    source = combined_parser_source()
    for forbidden in ("psutil", "requests", "OpenProcess", "extract_database_keys_windows", "decrypt_database_tree", "WECHAT_MONITOR_SERVER_TOKEN"):
        assert forbidden not in source
```

Also cover schema mismatch, hash mismatch, duplicate relative path, 5,000-message truncation, deterministic sorting, malformed SQLite isolation, output-size limit, cancellation signal, and fixture equivalence.

- [x] **Step 3: Verify RED**

Run: `python3 -m pytest -q windows-parser/tests`  
Expected: FAIL because the pure parser does not exist.

- [x] **Step 4: Implement only pure reader behavior**

Move and adapt the message/contact/favorite normalization helpers from `windows/wechat_decrypt_engine.py`. Open SQLite read-only with `mode=ro&immutable=1`, set `query_only=ON`, enforce a progress-handler deadline, and close every connection. The CLI accepts exactly `--job <path>`, verifies all job paths and hashes, writes result JSON to a temporary file below `outputRoot`, flushes it, atomically renames to `result.json`, then prints one compact success document containing `resultPath`, `jobId`, and `sourceSetId`.

- [x] **Step 5: Pass parser tests and commit**

Run: `python3 -m pytest -q windows-parser/tests`  
Expected: PASS.  
Commit: `feat: extract pure database parser`

### Task 4: Build controlled parser jobs and supervise the process

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/ParserJobBuilder.cs`
- Create: `windows-background/src/DesktopPet.DataSync/ParserProcessSupervisor.cs`
- Create: `windows-background/src/DesktopPet.DataSync/ParserContracts.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ParserJobBuilderTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ParserProcessSupervisorTests.cs`

**Interfaces:**
- Consumes: a claimed parse job and its fixed catalog generation list.
- Produces: a controlled input tree, schema-v1 job JSON, bounded parser result, and structured terminal status.

- [x] **Step 1: Write failing containment and lifecycle tests**

Prove relative-path reconstruction, hardlink-or-copy hash preservation, no overwrite, parser executable hash allowlist, sanitized environment, 120-second soft cancellation, 180-second process-tree kill, 64-KiB stderr cap, cancellation propagation, and lease preservation after crash.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "ParserJobBuilderTests|ParserProcessSupervisorTests"`  
Expected: FAIL because job construction and supervision do not exist.

- [x] **Step 3: Implement controlled execution**

Build `DataSync/Jobs/<jobId>/input/<relativePath>` from catalog files. Prefer a same-volume hardlink; otherwise copy to a temporary file, flush, verify SHA-256, and rename. Start only the configured `wx_parser.exe` whose SHA-256 matches `parser-install.json`. Set the working directory to the job root, clear inherited proxy/server/key environment names, use redirected stdout/stderr, and kill the entire process tree at the hard deadline.

- [x] **Step 4: Pass tests and commit**

Run the focused tests above.  
Expected: PASS.  
Commit: `feat: supervise bounded parser jobs`

### Task 5: Validate parser output and commit incremental Outbox work

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/ParserResultValidator.cs`
- Create: `windows-background/src/DesktopPet.DataSync/IncrementalOutboxWriter.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ParserResultValidatorTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/IncrementalOutboxWriterTests.cs`

**Interfaces:**
- Consumes: `ParserResultDocument` schema 1 with matching job/source-set identity.
- Produces: unique exported-item identities, encrypted endpoint-specific Outbox batches, completed parse job, and no duplicate work on replay.

- [x] **Step 1: Write failing validation and transaction tests**

Reject mismatched job/source-set IDs, extra JSON members carrying absolute paths, over-limit arrays, over-limit media, invalid base64, timestamps outside signed 64-bit range, duplicate items, output above 32 MiB, and item strings above 64 KiB. Simulate a crash after Outbox insert but before parse completion and prove transaction rollback leaves neither exported identities nor Outbox rows.

```csharp
[Fact]
public async Task ReplayingSameParserResultCreatesNoDuplicateOutboxRows()
{
    await _writer.CommitAsync(_job, _result, default);
    await _writer.CommitAsync(_job, _result, default);
    Assert.Equal(_expectedItemCount, await _repository.CountExportedItemsAsync(default));
    Assert.Equal(_expectedBatchCount, await _repository.CountOutboxAsync(default));
}
```

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "ParserResultValidatorTests|IncrementalOutboxWriterTests"`  
Expected: FAIL.

- [x] **Step 3: Implement stable identities and atomic commit**

Message identity is SHA-256 of normalized `wxid|local_id|create_time|is_sender|sender|content|msg_type|msg_sub_type|media_sha256`. Contact identity is `wxid`; favorite identity is `source_table|source_id`. Insert each unseen identity and its encrypted Outbox batch in the same immediate transaction, then mark the parse job complete. Batch at 500 messages, 1,000 contacts, or 500 favorites and cap every plaintext request body at 8 MiB before protection.

- [x] **Step 4: Pass tests and commit**

Run the focused tests above.  
Expected: PASS.  
Commit: `feat: commit incremental parser results to outbox`

### Task 6: Add protected server settings and durable upload classification

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/Upload/ServerSettingsVault.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Upload/OutboxUploader.cs`
- Create: `windows-background/src/DesktopPet.DataSync/Upload/UploadModels.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ServerSettingsVaultTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/OutboxUploaderTests.cs`

**Interfaces:**
- Consumes: current-user-DPAPI settings at `DataSync/server-settings.dpapi` and claimed encrypted Outbox rows.
- Produces: acknowledged, retry-scheduled, or quarantined Outbox state with stable request IDs.

- [x] **Step 1: Write failing protection and HTTP classification tests**

Cover missing settings staying offline without dropping Outbox, settings tamper detection, token absence from diagnostics, success acknowledgment, timeout, 408, 429 with `Retry-After`, 500, permanent 400/401/403/404, cancellation, duplicate server acknowledgment, and process restart during an in-flight claim.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter "ServerSettingsVaultTests|OutboxUploaderTests"`  
Expected: FAIL.

- [x] **Step 3: Implement uploader policy**

Decrypt one row only after a lease is committed. POST to the fixed endpoint selected by row type (`/api/messages`, `/api/contacts`, `/api/favorites`, `/api/status`, `/api/events`) with the stable request ID inside the body and token in the existing body field. Mark success only after a 2xx response body passes endpoint-specific JSON validation. Retry network failures, 408, 429, and 5xx with full-jitter exponential delay `min(2^attempt seconds, 15 minutes)`; quarantine other 4xx responses after storing only status code and a 256-character sanitized response summary.

- [x] **Step 4: Pass tests and commit**

Run the focused tests above.  
Expected: PASS.  
Commit: `feat: upload encrypted outbox durably`

### Task 7: Add the normal-privilege DataSync Worker host

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync.Worker/DesktopPet.DataSync.Worker.csproj`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncWorker.cs`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/HandoffReadyWatcher.cs`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncDiagnosticReader.cs`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/SingleInstanceGuard.cs`
- Create: `windows-background/src/DesktopPet.DataSync.Worker/worker.manifest`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncWorkerTests.cs`

**Interfaces:**
- Produces: `DesktopPet.DataSync.Worker.exe` with no arguments, `--once`, and `--diagnose`; mutex `Local\JunjieeDesktopPet-DataSync-v1`.

- [x] **Step 1: Write failing lifecycle tests**

Prove duplicate exit code 10, startup lease recovery, ready-manifest watch, two-second debounce, five-minute reconciliation, parser concurrency exactly one, upload concurrency exactly two, continuous operation without the WPF process, cancellation preserving claims, corrupt state stopping without data loss, and one sanitized diagnostic JSON object.

- [x] **Step 2: Verify RED**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter DataSyncWorkerTests`  
Expected: FAIL.

- [x] **Step 3: Implement Generic Host composition**

Use `Microsoft.Extensions.Hosting` 8.0.1. Watch only `Handoff/ready`, debounce two seconds, reconcile every five minutes, and poll due Outbox work every 15 seconds. Keep the Worker alive when settings are absent, the parser queue is empty, uploads fail, or WPF exits. `--diagnose` opens `sync.db` read-only and reports counts/modes only; it never decrypts settings or Outbox payloads.

- [x] **Step 4: Pass tests, build, and commit**

```text
dotnet test windows-background/DesktopPet.Background.sln
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
```

Expected: all tests PASS and Release build has 0 warnings/errors.  
Commit: `feat: add persistent datasync worker host`

### Task 8: Prove real-handoff-to-fake-server end to end and enforce size budget

**Files:**
- Create: `windows-background/tests/DesktopPet.DataSync.Tests/EndToEndDataSyncTests.cs`
- Create fixture: `windows-background/tests/DesktopPet.DataSync.Tests/fixtures/real-recovery-handoff/**`
- Create: `windows-parser/build-parser.ps1`
- Create: `docs/superpowers/reports/2026-07-25-datasync-parser-worker.md`

**Interfaces:**
- Consumes: a manifest produced by `AtomicHandoffPublisher`, the pure parser artifact, DPAPI test settings, and a loopback fake server.
- Produces: accepted handoff, completed parse job, acknowledged Outbox rows, exact server payload evidence, and artifact-size evidence.

- [x] **Step 1: Generate the fixture through production Recovery publisher**

Use real readable SQLite fixtures and `AtomicHandoffPublisher.PublishAsync`; do not hand-author generation or manifest IDs. Copy the resulting immutable generation files and ready JSON into the DataSync fixture directory and record every SHA-256 in the test.

- [x] **Step 2: Write the failing end-to-end test**

The test starts a standard-library loopback HTTP server, imports the real handoff, runs the packaged parser, commits incremental items, injects failures `500, 429, 200`, restarts DataSync between attempts, then asserts one accepted marker, one completed source set, no duplicate exported identities, all Outbox rows acknowledged, and exact message/contact/favorite request IDs received once logically.

- [x] **Step 3: Verify RED, then close gaps only in owning modules**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter EndToEndDataSyncTests`  
Expected before final fixes: FAIL at the first incomplete integration boundary. Make only task-specific fixes and rerun until PASS.

- [x] **Step 4: Build the parser and measure the complete payload**

`build-parser.ps1` creates a clean PyInstaller `--onedir` artifact using only `wx_parser.py`, `parser_core.py`, and standard-library imports. Measure the zipped parser plus both framework-dependent Workers and shared assemblies. Fail the build script when the ZIP exceeds 85 MiB.

- [x] **Step 5: Run final gates and write report**

```text
python3 -m pytest -q windows-parser/tests
python3 -m pytest -q tests
dotnet test windows-background/DesktopPet.Background.sln
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
git diff --check
```

Report test totals, real handoff hashes, parser SHA-256, compressed/uncompressed sizes, retry sequence, remaining Windows-only checks, and confirmation that WPF/installer/current runtime were untouched.

- [x] **Step 6: Commit**

Commit: `test: close datasync parser foundation`

## Self-Review Result

- Spec coverage: actual schema-v1 handoff import, durable leases, pure parser, incremental identities, encrypted Outbox, upload classification, persistent Worker, fake-server E2E, and size gate each map to a task.
- Boundary coverage: Recovery never receives server settings; DataSync never receives keys; parser receives neither.
- Type consistency: `manifestId`, `generationId`, `sourceSetId`, `jobId`, and Outbox `idempotencyKey` retain one meaning across all tasks.
- Cutover rule: no task modifies WPF startup or installer behavior; a separate media-and-installer plan begins only after Task 8 passes.
- Placeholder scan: the plan contains no deferred implementation markers; external image bytes are explicitly outside this foundation and represented as metadata-only output.
