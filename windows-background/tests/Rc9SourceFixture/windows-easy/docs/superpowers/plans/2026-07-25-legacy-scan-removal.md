# RC8 Single Precise Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish RC8 with one precise-capture recovery path and evidence results aligned with that path.

**Architecture:** Move automatic process discovery behind `TargetProcessDiscovery`, then delete the disconnected legacy scan stack. Keep the four displayed evidence rows for readable continuity, but make Gate A explicitly not applicable and calculate overall status from Gates B and C only.

**Tech Stack:** .NET 8, C#, WinForms, xUnit, PowerShell publish profile, Windows x64 self-contained single-file.

## Global Constraints

- RC7 files and hashes must remain byte-for-byte unchanged.
- Keep the 180-second capture window, 5-minute early process wait, four callpoints, DPAPI vault, WAL validation, and atomic output behavior unchanged.
- RC8 does not implement persistent background continuation for unmatched databases.
- This workspace has no Git metadata, so test and artifact checkpoints replace commit steps.

---

### Task 1: Lock the RC8 contracts with failing tests

**Files:**
- Create: `tests/Wx411.Core.Tests/LegacyScanRemovalContractTests.cs`
- Modify: `tests/Wx411.Core.Tests/EvidenceAssessmentTests.cs`
- Modify: `tests/Wx411.Core.Tests/ReleaseContractTests.cs`

**Interfaces:**
- Consumes: `TestSourceTree.ReadWindowsEasy(string)` and `EvidenceGateEvaluator.Assess(EvidenceSessionSnapshot)`.
- Produces: source-level contracts for the sole capture button, `TargetProcessDiscovery`, removed legacy files, `N/A` Gate A, and B/C-only overall status.

- [ ] Write tests asserting `MainForm.cs` contains “刷新列表” and “定位 key 并解密”, and excludes both legacy buttons and `StartRecoveryAsync`.
- [ ] Write tests asserting legacy scan source files are absent and `CallpointCaptureRecoveryService.cs` calls `TargetProcessDiscovery.Discover()` without referencing `ProcessMemoryScanner`.
- [ ] Write evaluator tests asserting Gate A is `NotApplicable`, B+C pass yields overall pass even when D fails, and a B or C failure still fails overall.
- [ ] Run the focused tests and confirm they fail for the missing RC8 behavior.

### Task 2: Move process discovery and remove the production scan path

**Files:**
- Modify: `src/Wx411.Core/CallpointCaptureRecoveryService.cs`
- Modify: `src/Wx411.Core/RecoveryContracts.cs`
- Delete: `src/Wx411.Core/MemoryScanRecoveryService.cs`
- Delete: `src/Wx411.Core/ProcessMemoryScanner.cs`
- Delete: `src/Wx411.Core/KeyMaterialScanner.cs`
- Delete: `src/Wx411.Core/Aes256KeyScheduleScanner.cs`
- Delete: `src/Wx411.Core/CodecHolderScanner.cs`
- Delete: `src/Wx411.Core/ProcessScanDiagnostics.cs`

**Interfaces:**
- Consumes: `TargetProcessDiscovery.Discover(string processName = "Weixin")` returning `IReadOnlyList<TargetProcessSource>`.
- Produces: `ResolveTargets(RecoveryProcessSelection)` returning precise-capture process selections without any legacy scanner type.

- [ ] Replace the `ProcessInfo` path with `TargetProcessSource` and preserve PID ordering plus database-handle-owner priority.
- [ ] Add refreshed PIDs to `scheduledPids` only after `Weixin.dll` is loaded, so a late-loading main PID remains eligible on a later refresh.
- [ ] Delete `RecoveryResult` and `DiagnosticRecoveryException` after their final UI caller is removed.
- [ ] Delete the legacy production files.
- [ ] Build `Wx411.Core` and fix only references that prove a deleted legacy dependency remains.

### Task 3: Make MainForm a single precise-capture UI

**Files:**
- Modify: `src/Wx411.Easy/MainForm.cs`

**Interfaces:**
- Consumes: `CallpointCaptureRecoveryService.CaptureAndDecryptAsync(...)`.
- Produces: one primary command, `StartCaptureAsync()`, plus list refresh, cancellation, output access, and evidence export.

- [ ] Delete `_startButton`, `_diagnosticButton`, their click handlers, layout entries, busy-state handling, and `StartRecoveryAsync(bool)`.
- [ ] Change `_refreshButton.Text` and all related prompts from “重新扫描” to “刷新列表”.
- [ ] Set `AcceptButton = _captureButton` and update title/subtitle text to precise-capture-only wording.
- [ ] Run `LegacyScanRemovalContractTests` and `MainFormCallpointContractTests` and confirm the UI contract passes.

### Task 4: Retire Gate A and align overall evidence status

**Files:**
- Modify: `src/Wx411.Core/EvidenceModels.cs`
- Modify: `src/Wx411.Core/EvidenceBundleService.cs`
- Modify: `tests/Wx411.Core.Tests/EvidenceAssessmentTests.cs`
- Modify: `tests/Wx411.Core.Tests/EvidenceBundleServiceTests.cs`
- Modify: `tests/Wx411.Core.Tests/EvidenceSessionRecorderTests.cs`
- Modify: `tests/Wx411.Core.Tests/MainFormEvidenceContractTests.cs`

**Interfaces:**
- Produces: `EvidenceGateStatus.NotApplicable`; `EvidenceOperationKind.PreciseCapture` as the only operation kind; overall status based on Gate B and Gate C.

- [ ] Remove legacy evidence operation kinds and adapt generic recorder tests to precise capture.
- [ ] Remove the unreachable diagnostic file list from evidence records, recorder completion, bundle export, and their legacy tests.
- [ ] Return Gate A as `NotApplicable` with an explicit legacy-retirement detail.
- [ ] Exclude Gate A and Gate D from overall calculation while retaining their summary rows.
- [ ] Render `NotApplicable` as `N/A` in `SUMMARY.txt`.
- [ ] Run evidence-focused tests and confirm all status and packaging assertions pass.

### Task 5: Delete legacy-only tests and update user documentation

**Files:**
- Delete: `tests/Wx411.Core.Tests/AesScheduleSensitiveBufferTests.cs`
- Delete: `tests/Wx411.Core.Tests/Aes256KeyScheduleScannerTests.cs`
- Delete: `tests/Wx411.Core.Tests/CodecHolderScannerTests.cs`
- Delete: `tests/Wx411.Core.Tests/DiagnosticsTests.cs`
- Delete: `tests/Wx411.Core.Tests/KeyMaterialSensitiveBufferTests.cs`
- Delete: `tests/Wx411.Core.Tests/KeyScannerTests.cs`
- Delete: `tests/Wx411.Core.Tests/MainFormSensitiveBufferTests.cs`
- Delete: `tests/Wx411.Core.Tests/MemoryScanRecoveryServiceTests.cs`
- Delete: `tests/Wx411.Core.Tests/ProcessMemorySensitiveBufferTests.cs`
- Modify: `tests/Wx411.Core.Tests/MainFormCallpointContractTests.cs`
- Modify: `使用说明.txt`
- Modify: `诊断测试步骤.txt`
- Modify: `build-win-x64.ps1`

**Interfaces:**
- Produces: current instructions and release metadata with no legacy scan or 30-second compatibility path.

- [ ] Remove the one callpoint contract that inspects `MemoryScanRecoveryService`.
- [ ] Delete tests whose subject was removed with the scan stack.
- [ ] Rewrite instructions around refresh, cancellation, precise capture, evidence export, and B/C acceptance.
- [ ] Remove legacy scan claims from generated release-index text in the build script.
- [ ] Search active source, active tests, and current documentation for all legacy UI strings and types.

### Task 6: Verify and package RC8

**Files:**
- Create: `dist/refactor-rc8/TEST-STEPS.txt`
- Create: `dist/refactor-rc8/Wx411Easy-v1.5-refactor-rc8.exe`
- Create: `dist/refactor-rc8/Wx411Easy-v1.5-refactor-rc8.zip`
- Create: `dist/refactor-rc8/SHA256SUMS.txt`

**Interfaces:**
- Produces: a standalone Windows x64 GUI RC8 release containing the EXE and Chinese test instructions.

- [ ] Run `dotnet test Wx411Easy.sln -c Release -p:TreatWarningsAsErrors=true`.
- [ ] Run `dotnet format Wx411Easy.sln --verify-no-changes`.
- [ ] Publish with `src/Wx411.Easy/Properties/PublishProfiles/win-x64-single.pubxml`.
- [ ] Verify the EXE PE machine is `0x8664`, optional-header magic is `0x20b`, and subsystem is `2`.
- [ ] Package exactly the RC8 EXE and `TEST-STEPS.txt`, calculate SHA-256, and verify the ZIP entries and checksums.
- [ ] Recalculate the RC7 EXE and ZIP hashes and confirm they remain `f2ae40377a0aac547907c42db8475e6e86af955c49cde70ea7990caf3a1f44ec` and `87c9f2679b1386c9bf15d8289ad21dd3e4bbb31ac401a1420ee0477114d404f5`.
