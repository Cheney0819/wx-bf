# Recovery Worker Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Build a testable, persistent Windows Recovery Worker around the frozen RC9 core without switching the existing desktop-pet runtime path yet.

**Architecture:** A new windows-background solution owns shared contracts, persistence, recovery orchestration, and the elevated worker host. RC9 source is imported from the immutable source archive with provenance; a narrow validated-key sink preserves verified keys before RC9 clears sensitive buffers. This plan leaves the existing WPF runtime untouched.

**Tech Stack:** C# 12, .NET 8, Microsoft.Data.Sqlite 8.0.8, xUnit 2.9.2, Windows DPAPI, RC9 Wx411.Core.

## Global Constraints

- Ignore android-pet and do not stage its files.
- Never modify the frozen RC9 archive under /Users/jiee/Documents/Codex/2026-07-19/she-2.
- Verify source archive SHA-256 45bb644a0dce03331bc9c57901700d73e7976750b59ee03b20af50d7a3e40ebc before import.
- Current-process capture is attempt zero; only an initiated app restart consumes budget.
- An epoch permits exactly two app restarts, persisted before process control.
- Any validated key or RC9 pending capture permanently suppresses active restart for that epoch.
- Time, Worker/WPF/app restart, and Windows restart never reset an open circuit.
- Only executable version change, data-root identity change, or explicit RetryCapture creates a new epoch.
- Recovery has no server URL/token and performs no upload.
- App.xaml.cs, WeChatMonitor.cs, Python, installer, and the current runtime remain unchanged in this plan.
- Use TDD and task-scoped commits.

---

## Environment Preflight

The current macOS shell does not expose dotnet. Before Task 1, install an isolated .NET 8 SDK without modifying repository files:

~~~bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --quality GA --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
~~~

Expected: an 8.0.x SDK version. Record the exact SDK version in the phase report.

## File Map

~~~text
windows-background/
  DesktopPet.Background.sln
  Directory.Build.props
  IMPORTED-RC9.md
  src/
    Wx411.Core/
    DesktopPet.Background.Contracts/
    DesktopPet.Background.Infrastructure/
    DesktopPet.Recovery/
    DesktopPet.Recovery.Worker/
  tests/
    Wx411.Core.Tests/
    DesktopPet.Background.Tests/
    DesktopPet.Recovery.Tests/
~~~

### Task 1: Bootstrap solution and import frozen RC9 Core

**Files:**
- Create: windows-background/DesktopPet.Background.sln
- Create: windows-background/Directory.Build.props
- Create: windows-background/IMPORTED-RC9.md
- Create: windows-background/src/Wx411.Core/**
- Create: windows-background/tests/Wx411.Core.Tests/**

**Interfaces:**
- Consumes: immutable RC9 source ZIP with the required hash.
- Produces: buildable Wx411.Core and its original tests inside this repository.

- [ ] **Step 1: Verify archive hash**

Run:

~~~bash
SRC=/Users/jiee/Documents/Codex/2026-07-19/she-2/outputs/wx411_recover/windows-easy/archive/rc9-frozen-20260725/Wx411Easy-v1.5-refactor-rc9-source.zip
test "$(shasum -a 256 "$SRC" | awk '{print $1}')" = "45bb644a0dce03331bc9c57901700d73e7976750b59ee03b20af50d7a3e40ebc"
~~~

Expected: exit 0 with no output.

- [ ] **Step 2: Import only Core and Core tests**

~~~bash
rm -rf /tmp/desktop-pet-rc9-import
mkdir -p /tmp/desktop-pet-rc9-import
unzip -q "$SRC" -d /tmp/desktop-pet-rc9-import
mkdir -p windows-background/src windows-background/tests
cp -R /tmp/desktop-pet-rc9-import/src/Wx411.Core windows-background/src/
cp -R /tmp/desktop-pet-rc9-import/tests/Wx411.Core.Tests windows-background/tests/
~~~

Expected: no bin, obj, dist, Wx411.Easy, or WinForms source is imported.

- [ ] **Step 3: Add deterministic build properties**

Create Directory.Build.props:

~~~xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
~~~

- [ ] **Step 4: Record provenance**

IMPORTED-RC9.md records the source ZIP path/hash, release ZIP hash 5d1d5e3b8159b439e044598fa9ae35c4d37a5d006bd2dd7c27e60f474da6713d, imported paths, date, and an Integration deltas heading.

- [ ] **Step 5: Create solution and run imported tests**

~~~bash
dotnet new sln -n DesktopPet.Background -o windows-background
dotnet sln windows-background/DesktopPet.Background.sln add   windows-background/src/Wx411.Core/Wx411.Core.csproj   windows-background/tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj
dotnet test windows-background/tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj
~~~

Expected: original RC9 Core tests pass. Copy only test fixtures named by the imported test project if the archive omits them, and record their hashes.

- [ ] **Step 6: Commit**

~~~bash
git add windows-background
git commit -m "build: import frozen rc9 core"
~~~

### Task 2: Define contracts, paths, and atomic files

**Files:**
- Create: windows-background/src/DesktopPet.Background.Contracts/DesktopPet.Background.Contracts.csproj
- Create: windows-background/src/DesktopPet.Background.Contracts/RecoveryContracts.cs
- Create: windows-background/src/DesktopPet.Background.Infrastructure/DesktopPet.Background.Infrastructure.csproj
- Create: windows-background/src/DesktopPet.Background.Infrastructure/BackgroundPaths.cs
- Create: windows-background/src/DesktopPet.Background.Infrastructure/AtomicFile.cs
- Test: windows-background/tests/DesktopPet.Background.Tests/BackgroundPathsTests.cs
- Test: windows-background/tests/DesktopPet.Background.Tests/AtomicFileTests.cs

**Interfaces:**
- Produces: RecoveryEpochIdentity, RecoveryMode, CaptureObservation, DatabaseReadyManifest, BackgroundPaths.ForRoot, and AtomicFile.ReplaceAsync.

- [ ] **Step 1: Write failing tests**

~~~csharp
[Fact]
public void ForRoot_keeps_worker_ownership_separate()
{
    var paths = BackgroundPaths.ForRoot("/state");
    Assert.EndsWith(Path.Combine("Recovery", "recovery.db"), paths.RecoveryDatabase);
    Assert.EndsWith(Path.Combine("DataSync", "sync.db"), paths.SyncDatabase);
    Assert.NotEqual(paths.RecoveryDatabase, paths.SyncDatabase);
    Assert.EndsWith(Path.Combine("Handoff", "ready"), paths.HandoffReady);
}

[Fact]
public async Task Atomic_replace_never_leaves_temporary_file()
{
    var destination = Path.Combine(_root, "state.bin");
    await AtomicFile.ReplaceAsync(destination, "one"u8.ToArray(), default);
    await AtomicFile.ReplaceAsync(destination, "two"u8.ToArray(), default);
    Assert.Equal("two", await File.ReadAllTextAsync(destination));
    Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
}
~~~

- [ ] **Step 2: Verify the tests fail**

Run: dotnet test windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj

Expected: FAIL because projects/types do not exist.

- [ ] **Step 3: Implement exact public contracts**

~~~csharp
public enum RecoveryMode
{
    PassiveWaiting, CapturingCurrentProcess, RestartingForCapture,
    KeyMaterialAvailable, CaptureCircuitOpen, Degraded
}

public sealed record RecoveryEpochIdentity(string ExecutableVersion, string DataRootIdentity);

public sealed record CaptureObservation(
    bool HasValidatedKey,
    bool HasPendingCapture,
    IReadOnlyList<string> OutputPaths,
    string? FailureCode);

public sealed record DatabaseReadyItem(
    string GenerationId, string RelativePath, string PlaintextPath, string Sha256);

public sealed record DatabaseReadyManifest(
    int SchemaVersion, string ManifestId, string EpochId,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<DatabaseReadyItem> Databases);
~~~

- [ ] **Step 4: Implement atomic replacement**

AtomicFile writes a GUID temporary file beside the destination using FileOptions.WriteThrough, calls FlushAsync and Flush(true), then File.Move(temp, destination, true) in a try/finally that removes the temp file.

- [ ] **Step 5: Add projects and pass tests**

~~~bash
dotnet sln windows-background/DesktopPet.Background.sln add   windows-background/src/DesktopPet.Background.Contracts/DesktopPet.Background.Contracts.csproj   windows-background/src/DesktopPet.Background.Infrastructure/DesktopPet.Background.Infrastructure.csproj   windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj
dotnet test windows-background/tests/DesktopPet.Background.Tests/DesktopPet.Background.Tests.csproj
~~~

Expected: PASS.

- [ ] **Step 6: Commit**

~~~bash
git add windows-background
git commit -m "feat: add background contracts and paths"
~~~

### Task 3: Persist Recovery epochs and irreversible restart accounting

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/DesktopPet.Recovery.csproj
- Create: windows-background/src/DesktopPet.Recovery/Persistence/RecoverySchema.cs
- Create: windows-background/src/DesktopPet.Recovery/Persistence/RecoveryRepository.cs
- Create: windows-background/src/DesktopPet.Background.Infrastructure/SqliteConnectionFactory.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/RecoveryRepositoryTests.cs

**Interfaces:**
- Produces: IRecoveryRepository, RecoveryEpoch, BeginOrLoadEpochAsync, TryConsumeRestartAsync, MarkKeyAvailableAsync, OpenCircuitAsync, and RecordGenerationAsync.

- [ ] **Step 1: Write failing persistence tests**

~~~csharp
[Fact]
public async Task Restart_budget_survives_repository_reopen()
{
    await using var first = await OpenRepositoryAsync();
    var epoch = await first.BeginOrLoadEpochAsync(new("4.1.0", "root-a"), false, default);
    Assert.True(await first.TryConsumeRestartAsync(epoch.Id, default));
    Assert.True(await first.TryConsumeRestartAsync(epoch.Id, default));
    Assert.False(await first.TryConsumeRestartAsync(epoch.Id, default));
    await first.DisposeAsync();

    await using var reopened = await OpenRepositoryAsync();
    Assert.False(await reopened.TryConsumeRestartAsync(epoch.Id, default));
}

[Fact]
public async Task Explicit_retry_creates_epoch_without_deleting_history()
{
    await using var repository = await OpenRepositoryAsync();
    var first = await repository.BeginOrLoadEpochAsync(new("4.1.0", "root-a"), false, default);
    var second = await repository.BeginOrLoadEpochAsync(new("4.1.0", "root-a"), true, default);
    Assert.NotEqual(first.Id, second.Id);
    Assert.NotNull(await repository.GetEpochAsync(first.Id, default));
}
~~~

Also test that identical identity reuses the epoch and version/root changes create one.

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryRepositoryTests

Expected: FAIL because persistence does not exist.

- [ ] **Step 3: Create schema**

Create schema_info, recovery_epoch, database_generation, and runtime_event in one transaction. recovery_epoch enforces restart_count BETWEEN 0 AND 2 and stores suppression, mode, failure code, identity, and UTC timestamps.

- [ ] **Step 4: Implement atomic budget consumption**

Use exactly one conditional update:

~~~sql
UPDATE recovery_epoch
SET restart_count = restart_count + 1,
    mode = 'RestartingForCapture',
    updated_at_utc = $now
WHERE id = $id
  AND restart_count < 2
  AND active_restart_suppressed = 0;
~~~

Return true only when changes() is 1. MarkKeyAvailableAsync and OpenCircuitAsync both set active_restart_suppressed to 1.

- [ ] **Step 5: Pass focused tests**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryRepositoryTests

Expected: PASS across close/reopen.

- [ ] **Step 6: Commit**

~~~bash
git add windows-background
git commit -m "feat: persist recovery epochs and restart budget"
~~~

### Task 4: Persist validated keys and a critical circuit snapshot

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/Security/ISecretProtector.cs
- Create: windows-background/src/DesktopPet.Recovery/Security/DpapiSecretProtector.cs
- Create: windows-background/src/DesktopPet.Recovery/Security/ValidatedKeyVault.cs
- Create: windows-background/src/DesktopPet.Recovery/Persistence/CriticalRecoverySnapshot.cs
- Modify: windows-background/src/Wx411.Core/RecoveryContracts.cs
- Modify: windows-background/src/Wx411.Core/CallpointCaptureRecoveryService.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/ValidatedKeyVaultTests.cs
- Test: windows-background/tests/Wx411.Core.Tests/CallpointCaptureRecoveryServiceTests.cs

**Interfaces:**
- Produces: IValidatedDatabaseKeySink.Store(metadata, ReadOnlySpan<byte>) and ValidatedKeyVault.

- [ ] **Step 1: Write failing RC9 sink tests**

Prove the sink is called exactly once only after a completed export, receives 32 bytes, is not called on failed export, and the temporary copy is zeroed after Store returns.

- [ ] **Step 2: Write failing vault tests**

~~~csharp
[Fact]
public void Save_never_writes_plaintext_and_round_trips()
{
    var vault = new ValidatedKeyVault(_root, new ReversibleTestProtector());
    var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    var id = vault.Store(Metadata("message_0.db"), key);
    var raw = File.ReadAllBytes(vault.PathForTest(id));
    Assert.DoesNotContain(Convert.ToHexString(key), Convert.ToHexString(raw));
    Assert.Equal(key, vault.Load(id));
    Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
}
~~~

- [ ] **Step 3: Add the narrow RC9 integration contract**

~~~csharp
public sealed record ValidatedDatabaseKeyMetadata(
    string DatabasePath,
    DatabaseFileGeneration Generation,
    string CipherProfileId,
    string CallpointName);

public interface IValidatedDatabaseKeySink
{
    void Store(ValidatedDatabaseKeyMetadata metadata, ReadOnlySpan<byte> key);
}
~~~

Inject an optional sink into CallpointCaptureRecoveryService. For completed export only, preserve a separate key copy, call Store before ConfirmExport/deleting pending capture records, and zero the copy in finally. Do not alter capture, validation, export, or ticket semantics.

- [ ] **Step 4: Implement vault and snapshot**

ValidatedKeyVault uses DPAPI CurrentUser plus application entropy and one atomic envelope per metadata hash. CriticalRecoverySnapshot separately stores epoch identity, restart count, suppression, and mode after every budget/suppression transaction. Neither contains plaintext in logs or SQLite.

- [ ] **Step 5: Update provenance and pass tests**

~~~bash
dotnet test windows-background/tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj
dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter "ValidatedKeyVaultTests|CriticalRecoverySnapshotTests"
~~~

Expected: original RC9 tests and new tests PASS. Record both modified RC9 files under Integration deltas.

- [ ] **Step 6: Commit**

~~~bash
git add windows-background
git commit -m "feat: persist validated database keys"
~~~

### Task 5: Implement the three-round capture state machine

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/RecoveryPolicy.cs
- Create: windows-background/src/DesktopPet.Recovery/RecoveryStateMachine.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/RecoveryStateMachineTests.cs

**Interfaces:**
- Produces: RecoveryAction with CaptureCurrent, RestartAndCapture, WaitPassively, or PublishOutputs.

- [ ] **Step 1: Write the transition matrix**

~~~csharp
[Theory]
[InlineData(0, false, false, false, RecoveryActionKind.RestartAndCapture)]
[InlineData(1, false, false, false, RecoveryActionKind.RestartAndCapture)]
[InlineData(2, false, false, false, RecoveryActionKind.WaitPassively)]
[InlineData(0, true,  false, true,  RecoveryActionKind.PublishOutputs)]
[InlineData(0, true,  false, false, RecoveryActionKind.WaitPassively)]
[InlineData(0, false, true,  false, RecoveryActionKind.WaitPassively)]
public async Task Chooses_expected_action(
    int restarts, bool hasKey, bool hasPending, bool hasOutputs,
    RecoveryActionKind expected)
{
    var fixture = await RecoveryFixture.CreateAsync(restarts);
    var outputs = hasOutputs ? new[] { "/generation/message_0.db" } : [];
    var action = await fixture.Machine.ObserveAsync(
        fixture.Epoch.Id,
        new(hasKey, hasPending, outputs, hasKey ? null : "zero_key"),
        default);
    Assert.Equal(expected, action.Kind);
}
~~~

Add an ordering test proving budget persistence completes before process restart is requested.

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryStateMachineTests

Expected: FAIL.

- [ ] **Step 3: Implement deterministic policy**

The state machine has no timer/process APIs. It maps persisted state plus one observation to an action. A zero-key observation calls TryConsumeRestartAsync; false opens the circuit and returns WaitPassively. Key/pending observations suppress restart immediately.

- [ ] **Step 4: Pass tests and commit**

~~~bash
dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryStateMachineTests
git add windows-background
git commit -m "feat: add bounded capture state machine"
~~~

### Task 6: Reuse persisted keys for future database generations

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/PersistedKeyDecryptor.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/PersistedKeyDecryptorTests.cs

**Interfaces:**
- Consumes: ValidatedKeyVault, DatabaseSource candidates, DatabaseProbeDescriptor, CipherProfileProbe, and ConsistentDatabaseExporter.
- Produces: CaptureObservation with validated-key/output state before any active capture or restart is considered.

- [ ] **Step 1: Write failing persisted-key tests**

Use RC9 SQLCipher fixtures to prove: a stored key decrypts a changed generation without invoking capture or process restart; a wrong stored key is skipped; one database failure does not block another; all loaded key buffers are zeroed after probing; and the completed output passes SQLite integrity validation.

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter PersistedKeyDecryptorTests

Expected: FAIL because PersistedKeyDecryptor does not exist.

- [ ] **Step 3: Implement persisted-key probing**

For every unprocessed DatabaseSource generation, build a lightweight DatabaseProbeDescriptor. Load each distinct vault key one at a time, call CipherProfileProbe.FindMatch, and on a match pass a separate key copy plus the returned profile to ConsistentDatabaseExporter. Always zero the loaded and copied key in finally. Mark the database generation complete only after export and integrity validation succeed.

- [ ] **Step 4: Put reuse before capture**

RecoveryCoordinator first calls PersistedKeyDecryptor. If it produces any output, publish it and return passive. Only databases with no persisted-key match continue to RC9 capture. This rule is tested with fakes that assert capture/restart call counts remain zero when reuse succeeds.

- [ ] **Step 5: Pass tests and commit**

~~~bash
dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter PersistedKeyDecryptorTests
git add windows-background
git commit -m "feat: reuse keys for database generations"
~~~

### Task 7: Publish database generations through atomic handoff

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/AtomicHandoffPublisher.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/AtomicHandoffPublisherTests.cs

**Interfaces:**
- Produces: ready/manifest-id.json and immutable Recovery/Generations/generation-id files.

- [ ] **Step 1: Write failing tests**

Cover same-generation idempotency, new-content generation, SHA-256 match, cancellation before rename, and rejection of rooted or parent-traversal paths.

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter AtomicHandoffPublisherTests

Expected: FAIL.

- [ ] **Step 3: Implement publisher**

Generation ID is lowercase SHA-256 of epochId + relativePath + contentSha256. Copy and flush the immutable generation file, verify its hash, serialize schema 1, then use AtomicFile.ReplaceAsync for the ready manifest.

- [ ] **Step 4: Pass tests and commit**

~~~bash
dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter AtomicHandoffPublisherTests
git add windows-background
git commit -m "feat: publish atomic database handoffs"
~~~

### Task 8: Adapt RC9 and coordinate restart ordering

**Files:**
- Create: windows-background/src/DesktopPet.Recovery/IRecoveryCaptureAdapter.cs
- Create: windows-background/src/DesktopPet.Recovery/IAppProcessController.cs
- Create: windows-background/src/DesktopPet.Recovery/WindowsAppProcessController.cs
- Create: windows-background/src/DesktopPet.Recovery/WeChatIdentityProvider.cs
- Create: windows-background/src/DesktopPet.Recovery/Rc9CaptureAdapter.cs
- Create: windows-background/src/DesktopPet.Recovery/RecoveryCoordinator.cs
- Test: windows-background/tests/DesktopPet.Recovery.Tests/RecoveryCoordinatorTests.cs

**Interfaces:**
- IRecoveryCaptureAdapter.CaptureAsync(RecoveryEpoch, CancellationToken) returns CaptureObservation.
- IAppProcessController.RestartAsync(CancellationToken) returns AppProcessIdentity. WindowsAppProcessController snapshots the current executable path, terminates the known process tree, waits for exit with a hard timeout, and starts only that snapshotted executable in the same interactive session.
- RecoveryCoordinator.RunEpochAsync serially executes state actions.

- [ ] **Step 1: Write failing sequence tests**

Test these exact event sequences:

~~~text
capture zero -> persist restart 1 -> restart -> capture zero
-> persist restart 2 -> restart -> capture zero -> open circuit -> passive

capture valid key + outputs -> publish -> passive, zero restart calls
capture pending -> passive, zero restart calls
restart throws after persisted consumption -> next run has one less budget
~~~

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryCoordinatorTests

Expected: FAIL.

- [ ] **Step 3: Implement RC9 adapter**

Discover only configured/known xwechat_files roots, select all DatabaseSource candidates, use RecoveryProcessSelection(null, "Automatic", ScanAll: true), PendingCaptureVault with WindowsDpapiProtector, and CallpointCaptureRecoveryService. Snapshot pending IDs before/after so a thrown zero-output run still reports pending. Map exception types/stages to stable codes without parsing localized log text.

- [ ] **Step 4: Implement coordinator**

Serialize with one SemaphoreSlim. Stop on PublishOutputs or WaitPassively. Propagate cancellation. Persist bounded diagnostics for other exceptions and never restore restart budget.

WeChatIdentityProvider computes epoch identity from the signed executable version plus a stable hash of the selected xwechat_files data root. It never emits the absolute root path in status or diagnostics.

- [ ] **Step 5: Pass tests and commit**

~~~bash
dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter "RecoveryCoordinatorTests|RecoveryStateMachineTests"
git add windows-background
git commit -m "feat: coordinate rc9 recovery attempts"
~~~

### Task 9: Add the long-running Worker host and diagnostic mode

**Files:**
- Create: windows-background/src/DesktopPet.Recovery.Worker/DesktopPet.Recovery.Worker.csproj
- Create: windows-background/src/DesktopPet.Recovery.Worker/Program.cs
- Create: windows-background/src/DesktopPet.Recovery.Worker/RecoveryWorker.cs
- Create: windows-background/src/DesktopPet.Recovery.Worker/SingleInstanceGuard.cs
- Create: windows-background/src/DesktopPet.Recovery.Worker/ProcessStartWatcher.cs
- Create: windows-background/src/DesktopPet.Recovery.Worker/worker.manifest
- Test: windows-background/tests/DesktopPet.Recovery.Tests/RecoveryWorkerTests.cs

**Interfaces:**
- Produces DesktopPet.Recovery.Worker.exe with default, --once, and --diagnose modes. No arbitrary path/PID arguments.

- [ ] **Step 1: Write failing lifecycle tests**

Prove startup recovers repository/snapshot, duplicate instance exits 10, --once runs one bounded epoch, default remains passive after circuit open, and cancellation does not restore budget.

- [ ] **Step 2: Verify failure**

Run: dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter RecoveryWorkerTests

Expected: FAIL.

- [ ] **Step 3: Implement host**

Use Microsoft.Extensions.Hosting 8.0.1 and mutex Local\JunjieeDesktopPet-Recovery-v1. --diagnose opens state read-only and emits one JSON object without keys or absolute user paths. ProcessStartWatcher compares the Weixin PID set once per second and emits only newly observed PIDs. Default mode combines those hints with known-root filesystem hints, debounces two seconds, and performs five-minute bounded reconciliation. Never scan a whole drive.

- [ ] **Step 4: Build and test**

~~~bash
dotnet sln windows-background/DesktopPet.Background.sln add   windows-background/src/DesktopPet.Recovery/DesktopPet.Recovery.csproj   windows-background/src/DesktopPet.Recovery.Worker/DesktopPet.Recovery.Worker.csproj   windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj
dotnet test windows-background/DesktopPet.Background.sln
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
~~~

Expected: tests PASS and Release build succeeds.

- [ ] **Step 5: Scan for leakage and placeholders**

~~~bash
rg -n "WECHAT_MONITOR_SERVER_TOKEN|SERVER_TOKEN|api/messages" windows-background
rg -n "catch\s*\{\s*\}" windows-background/src --glob "*.cs"
~~~

Expected: no credentials, endpoints, placeholders, or empty production catches.

- [ ] **Step 6: Commit**

~~~bash
git add windows-background
git commit -m "feat: add persistent recovery worker host"
~~~

### Task 10: Verify and close the phase

**Files:**
- Create: docs/superpowers/reports/2026-07-25-recovery-worker-foundation.md

**Interfaces:**
- Produces: verified report and exact handoff inputs for the DataSync/Parser plan.

- [ ] **Step 1: Run the complete automated gate**

~~~bash
dotnet test windows-background/DesktopPet.Background.sln
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
git diff --check
~~~

Expected: all pass.

- [ ] **Step 2: Write the report**

Record test totals, imported hashes, RC9 integration deltas, state-matrix coverage, artifact sizes, Windows-only paths not run locally, and confirmation that the old runtime was untouched.

- [ ] **Step 3: Commit**

~~~bash
git add docs/superpowers/reports/2026-07-25-recovery-worker-foundation.md
git commit -m "docs: close recovery worker foundation"
~~~

- [ ] **Step 4: Prepare the next plan**

Write docs/superpowers/plans/2026-07-25-datasync-parser-worker.md using the actual DatabaseReadyManifest schema. Installer cutover starts only after DataSync consumes a real handoff fixture.
