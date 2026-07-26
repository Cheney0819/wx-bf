# RC9 Precise Capture Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** COMPLETE on 2026-07-25. Automated verification: 230/230 PASS. Windows evidence: Gate B PASS, Gate C PASS, overall PASS. See `docs/superpowers/reports/2026-07-25-rc9-closure.md`.

**Goal:** Implement the revised RC9 specification so precise capture remains correct during login-time database replacement, uses bounded memory, cancels promptly, and never reports a clean detach until every live-process breakpoint is verified restored.

**Architecture:** RC9.1 hardens the Windows boundary with explicit PID semantics, a bounded native-path executor, one-generation module inspection, cancellable attach, verified breakpoint restoration, and a bounded sensitive candidate channel. RC9.2 replaces full database snapshots with a dynamic lightweight probe catalog and performs WAL-consistent authentication plus decryption one database at a time after detach.

**Tech Stack:** C# 12, .NET 8, WinForms, xUnit 2.9, Windows x64 Win32 debugging APIs, `System.Threading.Channels`, `Microsoft.Data.Sqlite`.

## Global Constraints

- Do not restore legacy memory scanning or the fixed 30-second compatibility check.
- Keep one recovery entry point and do not add cross-start/background continuation.
- Preserve four-callpoint capture, module SHA-256, signatures, SQLCipher page HMAC, `integrity_check`, DPAPI pending tickets, atomic output, evidence gate semantics, and semantic export formats.
- The revised specification permits evidence export of key material, DPAPI plaintext, capture payload, source database files, and debug memory; RC9 does not add a new evidence format merely because this is permitted.
- Process one PID and export one database at a time.
- Keep every RC8 artifact byte-for-byte unchanged; publish RC9 only under `dist/refactor-rc9/`.
- There is no Git metadata. Replace commit steps with named verification checkpoints and file hashes.

---

### Task 1: Baseline And Specification Guard

**Files:**
- Create: `tests/Wx411.Core.Tests/Rc9SpecificationContractTests.cs`
- Modify: `docs/superpowers/plans/2026-07-25-rc9-hardening.md`

**Interfaces:**
- Consumes: `docs/superpowers/specs/2026-07-25-rc9-hardening-design.md` SHA-256 `4209d9f945adff3eb63a32b8882da20d3bd8653fdad01daf3c4a7b1fee2958d2`.
- Produces: source-level guards for removed legacy paths, dynamic probes, streaming exporter, and release isolation.

- [ ] **Step 1: Record baseline**

Run: `dotnet test Wx411Easy.sln -c Release --no-restore`

Expected: the RC8 suite passes before any production edit.

- [ ] **Step 2: Write failing RC9 contract tests**

Add tests asserting that `CallpointCaptureRecoveryService` no longer references `StableDatabaseSnapshot.Read`, that `DatabaseCaptureTarget` has no full snapshot field, and that the service references `DatabaseProbeCatalog`, `CapturedCandidateChannel`, and `ConsistentDatabaseExporter`.

- [ ] **Step 3: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter Rc9SpecificationContractTests`

Expected: FAIL because RC9 production types do not exist and the old full-snapshot path remains.

### Task 2: Candidate PID And Bounded Handle Queries (RC9.1)

**Files:**
- Create: `src/Wx411.Core/Windows/BoundedHandlePathQueryExecutor.cs`
- Create: `tests/Wx411.Core.Tests/BoundedHandlePathQueryExecutorTests.cs`
- Modify: `src/Wx411.Core/Windows/ProcessFileHandleFinder.cs`
- Modify: `tests/Wx411.Core.Tests/ProcessFileHandleFinderContractTests.cs`

**Interfaces:**
- Produces: `ProcessFileHandleFinder.FindProcessIdsHoldingFile(string, IReadOnlyCollection<int>? candidatePids = null)` with `null = global`, empty = immediate empty, non-empty = filtered.
- Produces: internal `BoundedHandlePathQueryExecutor(int workerCount, int capacity, Func<nint,string> query, Action<nint> close)` and `HandlePathQueryResult TryQuery(nint handle, TimeSpan timeout)`.
- Produces: `HandlePathQueryStatus` values `Success`, `Failed`, `TimedOut`, and `Rejected`.

- [ ] **Step 1: Write failing tests**

Test an empty PID collection through an injected system-query seam and assert zero calls. Test executor concurrency never exceeds two, queue capacity never exceeds two, timed-out workers do not create replacement workers, and every accepted/rejected handle has exactly one owner that closes it.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter "ProcessFileHandleFinderContractTests|BoundedHandlePathQueryExecutorTests"`

Expected: FAIL on missing executor and empty-PID global-scan behavior.

- [ ] **Step 3: Implement bounded ownership**

Use two background worker threads and a bounded `Channel<HandlePathQueryWorkItem>` of capacity two. `TryQuery` transfers duplicated-handle ownership only after `TryWrite`; rejection closes synchronously. A timeout returns `TimedOut`, causes `ProcessFileHandleFinder` to stop the current enumeration, and leaves at most two native calls blocked for the process lifetime.

- [ ] **Step 4: Verify GREEN**

Run the filtered tests, then `dotnet test Wx411Easy.sln -c Release --no-restore`.

Expected: all tests pass with no warning.

### Task 3: Single-Generation Module Inspection (RC9.1)

**Files:**
- Create: `src/Wx411.Core/ModuleInspectionCache.cs`
- Create: `tests/Wx411.Core.Tests/ModuleInspectionCacheTests.cs`
- Modify: `src/Wx411.Core/PeCallpointLocator.cs`
- Modify: `src/Wx411.Core/Windows/DebugCaptureBackend.cs`

**Interfaces:**
- Produces: `ModuleFileGeneration(string NormalizedPath, long Length, DateTime LastWriteTimeUtc)`.
- Produces: `ModuleInspectionResult(ModuleFileGeneration Generation, ModuleIdentityValidation Identity, IReadOnlyList<CallpointDefinition> VerifiedCallpoints, string? Error)`.
- Produces: `ModuleInspectionCache.Inspect(string path, IReadOnlyCollection<string> requestedCallpointNames, CancellationToken token = default)`.

- [ ] **Step 1: Write failing tests**

Create a minimal AMD64 PE fixture with multiple signatures. Count reads through an internal stream factory and assert one complete read per unchanged generation, cache reuse on a second inspection, cache invalidation after length/time changes, and rejection when pre/post generation markers differ.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter ModuleInspectionCacheTests`

Expected: FAIL because the cache is absent.

- [ ] **Step 3: Implement and integrate**

Read the module once into one generation-scoped buffer, calculate SHA-256 from that buffer, parse the PE section table with `PeCallpointLocator.RvaToFileOffset`, verify requested signature slices, clear the buffer, and compare file generation again. Replace the backend's `ValidateModuleIdentity` plus per-callpoint `VerifySignature` loop with one cache lookup.

- [ ] **Step 4: Verify GREEN**

Run the filtered test and full Release suite.

### Task 4: Cancellable Attach And Verified Breakpoint Restoration (RC9.1)

**Files:**
- Create: `src/Wx411.Core/Windows/BreakpointRestorer.cs`
- Create: `tests/Wx411.Core.Tests/BreakpointRestorerTests.cs`
- Modify: `src/Wx411.Core/Windows/DebugCaptureBackend.cs`
- Modify: `src/Wx411.Core/Windows/ICallpointCaptureBackend.cs`
- Modify: `tests/Wx411.Core.Tests/DebugCaptureBackendContractTests.cs`

**Interfaces:**
- Produces: `BreakpointRestoreStatus` values `Restored`, `ProcessExited`, and `Fatal`.
- Produces: `BreakpointRestoreResult(BreakpointRestoreStatus Status, int Pid, nint Address, int Attempts, string? Error)`.
- Produces: `BreakpointRestorer.Restore(BreakpointRestoreRequest request, CancellationToken cleanupToken)` which performs write, flush, read-back in order, retries with a reopened process handle, and keeps retrying a live process until restored.
- Changes: `TryAttach(uint pid, CancellationToken token, out string error)`.

- [ ] **Step 1: Write failing tests**

Use fake native operations to assert exact write/flush/read order, retry after first write/read failure, reopened handle use, `ProcessExited` classification, fatal live-process state, and cancellation during attach delay in under one second.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter "BreakpointRestorerTests|DebugCaptureBackendContractTests"`

Expected: FAIL on missing structured restoration and cancellable attach.

- [ ] **Step 3: Implement safe cleanup**

Pass the caller token into attach. Replace `Thread.Sleep` with `token.WaitHandle.WaitOne(delay)` plus `ThrowIfCancellationRequested`. Route every breakpoint removal through `BreakpointRestorer`; report `正在恢复 PID ...` while retrying. Call `DebugActiveProcessStop` only after all live-process breakpoints return `Restored` or the target returns `ProcessExited`.

- [ ] **Step 4: Verify GREEN And RC9.1 Gate**

Run: `dotnet test Wx411Easy.sln -c Release --no-restore /warnaserror` and `dotnet format Wx411Easy.sln --verify-no-changes --no-restore`.

Expected: RC9.1 tests and all RC8 regression tests pass before Task 5 begins.

### Task 5: Bounded Sensitive Candidate Channel (RC9.1 Foundation)

**Files:**
- Create: `src/Wx411.Core/CapturedCandidateChannel.cs`
- Create: `tests/Wx411.Core.Tests/CapturedCandidateChannelTests.cs`
- Modify: `src/Wx411.Core/Windows/ICallpointCaptureBackend.cs`
- Modify: `src/Wx411.Core/Windows/DebugCaptureBackend.cs`

**Interfaces:**
- Produces: `CapturedCandidateChannel` with constant `Capacity = 64`, `MaxPayloadBytes = 4096`, `TryWrite(CapturedKeyMaterial candidate)`, `ReadAllAsync(CancellationToken)`, `Complete()`, and `DisposeAsync()`.
- Produces: `CaptureSessionErrorKind.CandidateQueueOverflow` and structured `CaptureSessionError`.
- Ownership: successful write transfers candidate ownership to channel; successful read transfers it to consumer; failed write, cancellation, completion, and disposal clear all remaining payloads.

- [ ] **Step 1: Write failing ownership tests**

Assert capacity 64, payload ceiling 4096, FIFO transfer, overflow classification, failed-write disposal, cancellation cleanup, and disposal cleanup of unread candidates using test buffers inspected after disposal.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter CapturedCandidateChannelTests`

Expected: FAIL because the channel is absent.

- [ ] **Step 3: Implement and isolate event-thread work**

The debug event handler only reads registers/process memory, creates one bounded candidate, tries channel ownership transfer, restores the breakpoint, updates thread context, and continues the debug event. It does not call the collector, discover files, hash pages, open SQLite, or write output.

- [ ] **Step 4: Verify GREEN**

Run filtered tests and full Release suite.

### Task 6: Lightweight Dynamic Database Probe Catalog (RC9.2)

**Files:**
- Create: `src/Wx411.Core/DatabaseProbeCatalog.cs`
- Create: `tests/Wx411.Core.Tests/DatabaseProbeCatalogTests.cs`
- Modify: `src/Wx411.Core/CipherProfileProbe.cs`

**Interfaces:**
- Produces: `DatabaseFileGeneration(long Length, DateTime LastWriteTimeUtc, string FileIdentity)`.
- Produces: disposable `DatabaseProbeDescriptor` containing normalized path, generation, length, 16-byte salt, compatible profiles, and only selected encrypted sample pages.
- Produces: `DatabaseProbeCatalog.Create(selectedPath, discoveredPaths, ...)`, `RefreshAsync(...)`, `Descriptors`, `SkippedPaths`, and `MarkPending/MarkConfirmed/MarkUnavailable`.
- Produces: sample verification overload accepting `(IReadOnlyDictionary<int,byte[]> pages, long databaseLength, rawKey, salt, profiles, ...)` without a full database array.

- [ ] **Step 1: Write failing tests**

Assert descriptor reads only salt plus pages 2, 1, middle, last; retries if generation changes; selected failure is fatal while another failure is skipped; adds a new `.db`; replaces an unconfirmed same-path generation; preserves confirmed state; marks deletion unavailable; and clears replaced/disposed samples.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter DatabaseProbeCatalogTests`

Expected: FAIL because the catalog is absent.

- [ ] **Step 3: Implement fixed-memory descriptors**

Open with `FileShare.ReadWrite | FileShare.Delete`, capture pre/post length/time/file identity, use seeked fixed-page reads, derive compatible profiles from length, retry a changed generation up to three times with cancellable 50 ms delays, and never call `File.ReadAllBytes` or `StableDatabaseSnapshot.Read`.

- [ ] **Step 4: Verify GREEN**

Run filtered tests and full suite.

### Task 7: Dynamic Collector And Pending-Key Lifecycle (RC9.2)

**Files:**
- Modify: `src/Wx411.Core/MultiDatabaseCaptureCollector.cs`
- Modify: `tests/Wx411.Core.Tests/MultiDatabaseCaptureCollectorTests.cs`

**Interfaces:**
- Changes: `DatabaseCaptureTarget` owns a `DatabaseProbeDescriptor`, pending keys, sample match metadata, and confirmed/export state; it owns no full snapshot.
- Produces: `Synchronize(DatabaseProbeCatalogSnapshot snapshot)`, `TryCollect(CapturedKeyMaterial, ...)`, `PendingExports`, `RetainPendingAfterGenerationFailure`, and `Release`.

- [ ] **Step 1: Write failing tests**

Replace snapshot-based fixtures with descriptors. Assert dynamic additions can match later candidates, replacement salt invalidates old sample caches, early match clears every normalized candidate (including lazily produced unenumerated values), duplicate keys are not retained twice, and one target failure does not mutate another target.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter MultiDatabaseCaptureCollectorTests`

Expected: FAIL against the old snapshot-owning collector.

- [ ] **Step 3: Implement descriptor collector**

Materialize normalized candidates inside one `using` ownership scope before iterating targets so every candidate is disposed even on early success. Match only descriptor sample pages, copy a 32-byte raw key only when recording pending state, and keep the pending key across exporter generation/HMAC failures until the session ends.

- [ ] **Step 4: Verify GREEN**

Run filtered tests and full suite.

### Task 8: WAL-Consistent Streaming Exporter (RC9.2)

**Files:**
- Create: `src/Wx411.Core/ConsistentDatabaseExporter.cs`
- Create: `src/Wx411.Core/StreamingSqlCipherDecryptor.cs`
- Create: `src/Wx411.Core/StreamingWalSnapshot.cs`
- Create: `tests/Wx411.Core.Tests/ConsistentDatabaseExporterTests.cs`
- Modify: `src/Wx411.Core/AtomicSqliteOutput.cs`

**Interfaces:**
- Produces: `DatabaseExportRequest(path, generation, rawKey owner, profile, outputDirectory)`.
- Produces: `DatabaseExportResult` with structured status `Completed`, `GenerationChanged`, `AuthenticationFailed`, `InsufficientSpace`, `OutputFailed`, or `Cancelled`.
- Produces: `ConsistentDatabaseExporter.ExportAsync(DatabaseExportRequest, IProgress<RecoveryProgress>?, CancellationToken)`.

- [ ] **Step 1: Write failing tests**

Assert committed checksum-continuous WAL overlay output, WAL generation rejection, page HMAC failure cleanup, disk-space preflight, occupied final output suffixing, cancellation cleanup, one-at-a-time temp file use, integrity check before move, and sparse-file processing without an array proportional to file length.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter ConsistentDatabaseExporterTests`

Expected: FAIL because no streaming exporter exists.

- [ ] **Step 3: Implement the streaming pipeline**

Scan WAL headers/frames with one reusable page buffer, validate salts/checksum chain, retain only latest committed page-to-frame offsets, create one random encrypted snapshot, copy/overlay pages with fixed buffers, authenticate and decrypt each page directly into one atomic output temp file, flush, run `integrity_check`, then move. Check free space before temp creation and delete encrypted/output temps plus SQLite sidecars in every non-completed path.

- [ ] **Step 4: Verify GREEN**

Run filtered tests and full suite.

### Task 9: Service Integration, UI Status, Documentation, And Release

**Files:**
- Modify: `src/Wx411.Core/CallpointCaptureRecoveryService.cs`
- Modify: `src/Wx411.Easy/MainForm.cs`
- Modify: `tests/Wx411.Core.Tests/CallpointCaptureRecoveryServiceTests.cs`
- Modify: `tests/Wx411.Core.Tests/MainFormCallpointContractTests.cs`
- Modify: `tests/Wx411.Core.Tests/Rc9SpecificationContractTests.cs`
- Create: `dist/refactor-rc9/TEST-STEPS.txt`
- Create: `dist/refactor-rc9/SHA256SUMS.txt`

**Interfaces:**
- Service flow: lightweight catalog -> load DPAPI tickets -> wait/attach -> event producer plus candidate consumer -> safe detach -> sequential exporter -> atomic result.
- Structured session errors replace string-substring terminal checks.

- [ ] **Step 1: Write failing integration tests**

Test zero full snapshots at capture start, refresh-before-candidate, periodic refresh, new/replaced database match, no exporter call for unmatched targets, overflow cleanup/error, cancellation propagation through each phase, continued export after one database failure, and retention of already completed outputs.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj -c Release --filter "CallpointCaptureRecoveryServiceTests|Rc9SpecificationContractTests|MainFormCallpointContractTests"`

Expected: FAIL until the service is rewired.

- [ ] **Step 3: Implement integration and Chinese operator steps**

Update progress text to tell a novice: click capture first, then start the target, wait for observation points, then log in. Show breakpoint recovery PID/status distinctly. Document cancellation tests, dynamic database generation tests, and large-database tests without restoring the removed scan/30-second flow.

- [ ] **Step 4: Full verification**

Run:

```bash
dotnet test Wx411Easy.sln -c Release --no-restore /warnaserror
dotnet format Wx411Easy.sln --verify-no-changes --no-restore
pwsh -File ./build-win-x64.ps1 -Configuration Release -OutputDirectory ./dist/refactor-rc9
```

Expected: all tests pass, formatting is clean, and the self-contained Windows x64 GUI executable is produced.

- [ ] **Step 5: Package and verify**

Create `Wx411Easy-v1.5-refactor-rc9.zip` containing only the RC9 EXE and Chinese `TEST-STEPS.txt`. Verify PE machine AMD64, GUI subsystem, ZIP listing, RC9 SHA-256 values, and the unchanged RC8 ZIP hash `cd9b1baafda6bb58f965078751ec4202088011b84ce0595057acd34d811ae8e6`.

- [ ] **Step 6: Windows real-machine gate**

Run the specification's waiting, cancellation, breakpoint restoration, login-time create/truncate/replace/WAL-write, multiple-PID, and large-database cases. Record CPU, thread/handle stability, peak working set, module inspection time, debug-event pause time, and export time before declaring RC9 final.
