# Continuous Multi-Database Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish each decrypted database batch immediately while capture continues automatically for every remaining database.

**Architecture:** Keep the five-second matched-key idle rule as a batch boundary. Track successful relative paths in `RecoveryCoordinator`, exclude them in later `Rc9CaptureAdapter` calls, and loop capture after publishing while the latest observation still has unfinished candidates.

**Tech Stack:** .NET 8, C# records/default interface methods, xUnit, Python parser tests, WPF/Inno Setup.

## Global Constraints

- Never stop the overall recovery cycle merely because one matched batch was idle for five seconds.
- Publish each successful batch before starting the next capture batch.
- Exclude only successfully recovered database paths; failed and unmatched paths remain eligible.
- Preserve existing 180-second no-match timeout, cancellation, restart circuit, handoff idempotency, and upload deduplication.
- Do not change Android or deploy the server.

---

### Task 1: Add exclusion-aware capture contract

**Files:**
- Modify: `windows-background/src/DesktopPet.Recovery/IRecoveryCaptureAdapter.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/Rc9CaptureAdapter.cs`
- Modify: `windows-background/tests/DesktopPet.Recovery.Tests/Rc9CaptureAdapterTests.cs`

**Interfaces:**
- Produces: `IRecoveryCaptureAdapter.CaptureAsync(RecoveryEpoch, RecoveryCaptureTarget, IReadOnlySet<string>, CancellationToken)`.
- Consumes: normalized relative paths such as `db_storage/hardlink/hardlink.db`.

- [ ] **Step 1: Write a failing adapter test**

Create two database sources, exclude the first relative path, invoke the new overload,
and assert the capture delegate receives only the second source while
`CandidateDatabaseCount` remains two.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
$HOME/.dotnet/dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Rc9CaptureAdapterTests.CompletedRelativePathsAreExcludedFromNextCaptureBatch
```

Expected: compile failure because the exclusion-aware overload does not exist.

- [ ] **Step 3: Implement the minimal overload**

Add a default interface overload that delegates to the existing target overload for
test fakes. Override it in `Rc9CaptureAdapter`, normalize discovered paths relative to
the bound data root, filter the supplied completed set, and retain the unfiltered
discovery count in the returned observation.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command. Expected: one passing test.

### Task 2: Continue capture after publishing partial batches

**Files:**
- Modify: `windows-background/src/DesktopPet.Recovery/RecoveryCoordinator.cs`
- Modify: `windows-background/tests/DesktopPet.Recovery.Tests/RecoveryCoordinatorTests.cs`

**Interfaces:**
- Consumes: the exclusion-aware capture overload from Task 1.
- Produces: multiple atomic handoff publications from one `RunEpochAsync` call.

- [ ] **Step 1: Write a failing coordinator test**

Configure a fake adapter to return an auxiliary recovered database with one unmatched
message database, then a recovered message database with no unmatched candidates.
Assert two capture calls, two handoff manifests, and that the second call receives the
auxiliary relative path in its completed set.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
$HOME/.dotnet/dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RecoveryCoordinatorTests.PartialBatchPublishesAndContinuesWithCompletedDatabaseExcluded
```

Expected: failure because `RunEpochAsync` returns after the first publication.

- [ ] **Step 3: Implement the capture loop**

Track recovered relative paths from persisted reuse and live observations. Pass a
snapshot into every bound or prepared capture call. After publishing a live batch,
set the next action to `CaptureCurrent` when unmatched or failed candidates remain;
otherwise return the completed action. Clear the continuation flag before each new
capture so a no-output failure follows existing state-machine behavior.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command. Expected: one passing test.

### Task 3: Lock the batch-boundary semantics and release

**Files:**
- Modify: `windows-background/tests/Wx411.Core.Tests/CallpointCaptureRecoveryServiceTests.cs`
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`

**Interfaces:**
- Preserves: `ShouldStopCurrentCapture(...)` as the matched-batch idle predicate.
- Produces: desktop version `1.0.24`.

- [ ] **Step 1: Rename the idle test around batch semantics**

Rename `CaptureMovesOnAfterMatchedDatabaseActivityBecomesIdle` to
`MatchedDatabaseIdleClosesOnlyTheCurrentCaptureBatch`; retain its truth table.

- [ ] **Step 2: Run complete verification**

```bash
python3 -m pytest windows-parser/tests -q
$HOME/.dotnet/dotnet test windows-background/DesktopPet.Background.sln -c Release --no-restore --verbosity minimal
$HOME/.dotnet/dotnet build windows-pet-wpf/DesktopPet.Wpf.csproj -c Release -r win-x64 --no-restore --verbosity minimal
git diff --check
```

Expected: all tests pass and WPF builds with zero warnings and errors.

- [ ] **Step 3: Bump, commit, push, and build**

Set all WPF and installer version fields to `1.0.24`, repeat the Release build, review
the diff, commit to `main`, push `origin/main`, trigger `build-windows.yml`, and verify
the `桌宠-1.0.24` artifact digest and size.
