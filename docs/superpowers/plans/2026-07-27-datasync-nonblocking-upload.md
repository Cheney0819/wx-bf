# DataSync Nonblocking Upload Implementation Plan

> **For agentic workers:** Execute with test-driven development and verify every
> claimed result from fresh command output.

**Goal:** Keep decrypt telemetry and business uploads moving while a large parser job
is still running.

**Architecture:** Preserve the single parser reconciliation loop and add one
independent continuous maintenance loop that imports telemetry and invokes the
existing two upload slots at the configured upload cadence.

**Tech Stack:** .NET 8, xUnit, Microsoft.Data.Sqlite, Python parser tests, WPF.

### Task 1: Prove the starvation regression

**Files:**
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncWorkerTests.cs`

1. Add a fake runtime whose first parser call blocks on a test-controlled gate.
2. Start continuous mode and wait until parsing is blocked.
3. Assert telemetry reconciliation and both upload slots execute before releasing
   the parser.
4. Run the focused test and confirm it fails against the current sequential loop.

### Task 2: Split continuous maintenance from parsing

**Files:**
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncWorker.cs`
- Modify: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncWorkerTests.cs`

1. Add one maintenance loop that immediately reconciles telemetry and polls uploads,
   then repeats after `UploadPollInterval`.
2. Remove telemetry and upload work from the continuous parser loop.
3. Isolate telemetry reconciliation failures so uploads still run, and contain any
   remaining non-cancellation maintenance failure until the next cadence.
4. Await maintenance shutdown alongside heartbeat and hint-source shutdown.
5. Run the focused worker tests until green.

### Task 3: Release verification

**Files:**
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`

1. Run all Windows background tests and parser tests.
2. Update the desktop version only after functional tests pass.
3. Run the WPF Release build and release validation.
4. Review the diff, commit, push `main`, and verify the GitHub Actions artifact.
