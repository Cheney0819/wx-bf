# One-Click Evidence Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-app button that exports all evidence recorded during the current session as an atomic, redacted, self-checking ZIP.

**Architecture:** Core owns immutable evidence models, file inspection, session lifecycle, gate assessment, and ZIP creation. `MainForm` only starts/completes evidence operations and passes its visible log to the exporter. Existing scanning, capture, cryptography, DPAPI, and database-output services remain unchanged.

**Tech Stack:** .NET 8, C# 12, System.IO.Compression, System.Text.Json, SHA-256, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- No database, SQLite, `.capture`, memory-dump, raw-key, or DPAPI-ciphertext payload may enter the ZIP.
- Database integrity checks run against a private temporary copy and leave no sidecar beside the original.
- Missing evidence produces a bundle with `INCOMPLETE` or `FAIL`; it does not abort the whole export unless the destination cannot be written.
- ZIP publication is atomic and cleans only temporary paths created by the exporter.
- The workspace has no `.git`; do not claim commits or branch integration.

---

### Task 1: Evidence Models and Gate Assessment

**Files:**
- Create: `src/Wx411.Core/EvidenceModels.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceAssessmentTests.cs`

**Interfaces:**
- Produces: `EvidenceOperationKind`, `EvidenceOperationOutcome`, `EvidenceGateStatus`, `EvidenceFileRecord`, `EvidenceDirectorySnapshot`, `EvidenceOperationRecord`, `EvidenceSessionSnapshot`, `EvidenceAssessment`, `EvidenceGateEvaluator.Assess(EvidenceSessionSnapshot)`.

- [x] **Step 1: Write failing tests** covering A success, B success, C incomplete without DPAPI proof, D matching/mismatching hashes, and overall status precedence.
- [x] **Step 2: Run** `dotnet test ... --filter FullyQualifiedName~EvidenceAssessmentTests`; expect compile failure because evidence types are absent.
- [x] **Step 3: Implement immutable records and pure evaluator.** A requires ordinary success, checked output and diagnostic; B requires precise success and checked output; C requires precise cancellation with clean after-directory but stays `INCOMPLETE` without structured DPAPI proof; D compares every existing before/after source hash.
- [x] **Step 4: Re-run targeted tests; expect all PASS.**

### Task 2: Non-Mutating File Inspection

**Files:**
- Create: `src/Wx411.Core/EvidenceFileInspector.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceFileInspectorTests.cs`

**Interfaces:**
- Produces: `Task<EvidenceFileRecord> InspectAsync(string path, bool verifySqlite, CancellationToken)` and `EvidenceDirectorySnapshot CaptureDirectory(string path)`.
- Consumes: models from Task 1 and `SqliteIntegrityChecker.VerifyFile`.

- [x] **Step 1: Write failing tests** for lowercase streaming SHA-256, missing file error, valid/corrupt SQLite, sorted directory entries, and temporary sidecar detection.
- [x] **Step 2: Run targeted tests; expect compile failure.**
- [x] **Step 3: Implement inspector.** Hash with shared-read `FileStream`; for SQLite create a unique system-temp directory, copy only the main file, verify the copy, then delete that exact directory in `finally`.
- [x] **Step 4: Assert test source/output directories contain no new `-wal`, `-shm`, or `.tmp` after inspection; re-run targeted tests.**

### Task 3: Session Recorder

**Files:**
- Create: `src/Wx411.Core/EvidenceSessionRecorder.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceSessionRecorderTests.cs`

**Interfaces:**
- Produces:
  - `Task<EvidenceOperationHandle> BeginAsync(EvidenceOperationKind, RecoveryProcessSelection, string sourcePath, string outputDirectory, CancellationToken)`
  - `Task CompleteAsync(EvidenceOperationHandle, EvidenceOperationOutcome, IEnumerable<string> outputPaths, IEnumerable<string> diagnosticPaths, Exception? error, CancellationToken)`
  - `EvidenceSessionSnapshot Snapshot()`
- Consumes: `EvidenceFileInspector` from Task 2.

- [x] **Step 1: Write failing tests** for begin/complete success, cancellation snapshot, failure metadata, unknown handle rejection, and detached immutable snapshots.
- [x] **Step 2: Run targeted tests; expect compile failure.**
- [x] **Step 3: Implement recorder with a private lock and mutable internal operation state.** Inspection occurs outside the lock; published snapshots deep-copy collections.
- [x] **Step 4: Run targeted tests; expect all PASS.**

### Task 4: Atomic Evidence ZIP

**Files:**
- Create: `src/Wx411.Core/EvidenceBundleService.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceBundleServiceTests.cs`

**Interfaces:**
- Produces: `Task<EvidenceBundleResult> ExportAsync(EvidenceSessionSnapshot session, string windowLog, string outputDirectory, CancellationToken)`.
- Produces ZIP entries `SUMMARY.txt`, `evidence.json`, `window-log.txt`, `SHA256SUMS.txt`, and optional `diagnostics/*.json`.

- [x] **Step 1: Write failing tests** for exact fixed entries, checksums, diagnostic collision names, key-hex redaction, forbidden extension exclusion, missing diagnostics, and failed-write temporary cleanup.
- [x] **Step 2: Run targeted tests; expect compile failure.**
- [x] **Step 3: Implement service.** Build byte arrays only for text/JSON evidence, redact labeled 64/96-hex key values, create ZIP at unique `.tmp`, validate entries after close, then move atomically.
- [x] **Step 4: Re-run targeted tests and inspect ZIP entries; expect all PASS and no leftover `.tmp`.**

### Task 5: MainForm Wiring

**Files:**
- Modify: `src/Wx411.Easy/MainForm.cs`
- Test: `tests/Wx411.Core.Tests/MainFormEvidenceContractTests.cs`

**Interfaces:**
- Consumes: `EvidenceSessionRecorder`, `EvidenceBundleService`.
- Adds UI command: `导出证据包`.

- [x] **Step 1: Write source-contract tests** requiring recorder initialization, begin/complete calls in ordinary/compatibility/precise paths, failure/cancel capture, export button, log export, and busy-state disabling.
- [x] **Step 2: Run targeted tests; expect assertion failure against current `MainForm`.**
- [x] **Step 3: Wire recorder lifecycle.** Begin before each operation; complete in success/catch paths with `CancellationToken.None`; keep evidence errors in log without replacing operation result.
- [x] **Step 4: Add `ExportEvidenceAsync`.** Export to selected output directory, log path, enable Explorer selection, and show one concise success/failure dialog.
- [x] **Step 5: Run targeted tests; expect all PASS.**

### Task 6: Verification and RC Packaging

**Files:**
- Modify: `dist/refactor-rc3/TEST-STEPS.txt`
- Create: `dist/refactor-rc3/SHA256SUMS.txt`
- Create: `dist/refactor-rc3/Wx411Easy-v1.5-refactor-rc3.exe`
- Create: `dist/refactor-rc3/Wx411Easy-v1.5-refactor-rc3.zip`

- [x] **Step 1: Run all tests in Release with warnings as errors; expect 0 failures.**
- [x] **Step 2: Build and publish win-x64 single-file; expect 0 warnings/errors and PE32+ GUI x86-64.**
- [x] **Step 3: Update Chinese test steps with evidence-button workflow and bundle contents.**
- [x] **Step 4: Package only EXE and test steps, write SHA-256, verify ZIP contents and byte identity.**
- [x] **Step 5: Preserve RC2 unchanged and report RC3 hashes.**
