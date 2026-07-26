# Pending Ticket Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Gate C automatically record PASS or FAIL after a precise-capture cancellation is followed by a successful precise capture in the same application session.

**Architecture:** `PendingCaptureVault` exposes metadata-only record IDs. `CallpointCaptureRecoveryService` reports IDs loaded from prior runs. `EvidenceSessionRecorder` privately correlates cancellation-created IDs with later loaded IDs, while `MainForm` only sequences snapshots and recorder calls.

**Tech Stack:** .NET 8, C# 12, WinForms, xUnit, DPAPI-backed existing vault.

## Global Constraints

- Do not automatically start recovery, capture, cancellation, or export operations.
- Do not read or export `.capture` contents, key material, DPAPI ciphertext, database payloads, or memory contents for this proof.
- Correlate operations only within one `EvidenceSessionRecorder` lifetime.
- Any metadata snapshot failure leaves proof `null` and Gate C `INCOMPLETE`.
- RC3 remains byte-for-byte unchanged; publish the fix as RC4.
- The workspace has no Git metadata; do not claim commits or branch integration.

---

### Task 1: Metadata-Only Vault Snapshot

**Files:**
- Modify: `src/Wx411.Core/PendingCaptureVault.cs`
- Test: `tests/Wx411.Core.Tests/PendingCaptureVaultTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<string> PendingCaptureVault.SnapshotRecordIds()`.
- Guarantees: sorted, distinct, lowercase 64-character hexadecimal IDs; does not call `Unprotect`.

- [x] **Step 1: Write failing tests**

Add tests that save two records, create malformed filenames, call `SnapshotRecordIds`, and assert only the two valid IDs are returned in ordinal order. Use a protector whose `Unprotect` throws and assert snapshot still succeeds.

```csharp
var ids = vault.SnapshotRecordIds();
Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), ids);
Assert.Equal(0, protector.UnprotectCalls);
```

- [x] **Step 2: Verify RED**

Run:

```bash
../../../work/dotnet/dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --no-restore --filter FullyQualifiedName~PendingCaptureVaultTests --verbosity minimal
```

Expected: compile failure because `SnapshotRecordIds` does not exist.

- [x] **Step 3: Implement minimal metadata enumeration**

Add:

```csharp
public IReadOnlyList<string> SnapshotRecordIds()
{
    if (!Directory.Exists(_root)) return Array.Empty<string>();
    var ids = Directory.EnumerateFiles(_root, "*.capture", SearchOption.AllDirectories)
        .Select(Path.GetFileNameWithoutExtension)
        .Where(id => id is { Length: 64 } && id.All(Uri.IsHexDigit))
        .Select(id => id.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToArray();
    return Array.AsReadOnly(ids);
}
```

Do not catch enumeration exceptions; callers need to distinguish unknown state from an empty vault.

- [x] **Step 4: Verify GREEN**

Run the Task 1 command. Expected: all `PendingCaptureVaultTests` pass.

---

### Task 2: Report Loaded Prior Ticket IDs

**Files:**
- Modify: `src/Wx411.Core/RecoveryContracts.cs`
- Modify: `src/Wx411.Core/CallpointCaptureRecoveryService.cs`
- Test: `tests/Wx411.Core.Tests/CallpointCaptureRecoveryServiceTests.cs`

**Interfaces:**
- Produces: `CaptureRecoveryResult.LoadedPendingCaptureTicketIds` as `IReadOnlyList<string>`.
- Consumes: record IDs returned by `PendingCaptureVault.LoadMatching`.

- [x] **Step 1: Write failing service tests**

Extend `PendingVaultMatchCompletesWithoutAttachingAndDeletesTicketAfterOutput`:

```csharp
var recordId = vault.Save(
    saltFingerprint,
    CallpointProfiles.Preferred.ModuleSha256,
    "sqlite3_key_equiv",
    key);
var result = await service.CaptureAndDecryptAsync(
    new RecoveryProcessSelection(null, "automatic", ScanAll: true),
    selected,
    [selected],
    outputDirectory,
    new Progress<RecoveryProgress>(),
    CancellationToken.None);
Assert.Equal(new[] { recordId }, result.LoadedPendingCaptureTicketIds);
```

Add a successful live-capture fixture assertion, where no vault record was loaded:

```csharp
Assert.Empty(result.LoadedPendingCaptureTicketIds);
```

- [x] **Step 2: Verify RED**

Run:

```bash
../../../work/dotnet/dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --no-restore --filter FullyQualifiedName~CallpointCaptureRecoveryServiceTests --verbosity minimal
```

Expected: compile failure because the result property is absent.

- [x] **Step 3: Extend the result contract**

Change the record to:

```csharp
public sealed record CaptureRecoveryResult(
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<DatabaseCaptureMatch> Matches,
    IReadOnlyList<string> UnmatchedDatabasePaths,
    IReadOnlyList<string> FailedDatabasePaths,
    IReadOnlyList<string> LoadedPendingCaptureTicketIds);
```

- [x] **Step 4: Track only loaded old records**

Create a case-insensitive set at method start, pass it into `LoadPendingCapturesFromVault`, and add each successfully loaded `record.RecordId`. Do not add IDs returned by `vault.Save`.

Return a sorted detached array:

```csharp
Array.AsReadOnly(loadedPendingCaptureTicketIds
    .OrderBy(id => id, StringComparer.Ordinal)
    .ToArray())
```

- [x] **Step 5: Verify GREEN**

Run the Task 2 command. Expected: all service tests pass.

---

### Task 3: Correlate Cancellation and Follow-Up Success

**Files:**
- Modify: `src/Wx411.Core/EvidenceSessionRecorder.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceSessionRecorderTests.cs`
- Test: `tests/Wx411.Core.Tests/EvidenceAssessmentTests.cs`

**Interfaces:**
- Produces: `void RecordCancelledPendingTickets(EvidenceOperationHandle handle, IReadOnlyCollection<string>? createdTicketIds)`.
- Produces: `void RecordPendingCaptureFollowUp(EvidenceOperationHandle successfulHandle, IEnumerable<string> loadedTicketIds)`.
- Consumes: completed precise-capture operation handles and loaded IDs from Task 2.

- [x] **Step 1: Write failing recorder tests**

Cover these separate cases:

```csharp
// Empty known cancellation set plus later success -> true.
recorder.RecordCancelledPendingTickets(cancelledHandle, Array.Empty<string>());
recorder.RecordPendingCaptureFollowUp(successHandle, Array.Empty<string>());
Assert.True(cancelled.PendingCaptureTicketNotReused);

// Disjoint IDs -> true.
recorder.RecordCancelledPendingTickets(cancelledHandle, new[] { "aa" });
recorder.RecordPendingCaptureFollowUp(successHandle, new[] { "bb" });

// Intersecting IDs -> false.
recorder.RecordCancelledPendingTickets(cancelledHandle, new[] { "AA" });
recorder.RecordPendingCaptureFollowUp(successHandle, new[] { "aa" });

// Unknown cancellation IDs -> null.
recorder.RecordCancelledPendingTickets(cancelledHandle, null);
recorder.RecordPendingCaptureFollowUp(successHandle, Array.Empty<string>());
```

Also assert invalid handles, wrong kinds, wrong outcomes, and success preceding cancellation are rejected or ignored without mutating unrelated operations.

- [x] **Step 2: Verify RED**

Run:

```bash
../../../work/dotnet/dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --no-restore --filter FullyQualifiedName~EvidenceSessionRecorderTests --verbosity minimal
```

Expected: compile failure because both APIs are absent.

- [x] **Step 3: Store private correlation IDs**

Add to `OperationState` only:

```csharp
public HashSet<string>? CreatedPendingCaptureTicketIds { get; set; }
```

Do not add this collection to `EvidenceOperationRecord` or JSON output.

- [x] **Step 4: Implement cancellation registration**

Under `_sync`, require the handle to identify a completed, cancelled `PreciseCapture`. Store `null` for unknown or a detached case-insensitive set for known IDs.

- [x] **Step 5: Implement follow-up correlation**

Under `_sync`, require `successfulHandle` to identify a completed successful `PreciseCapture`. For every earlier cancelled precise operation with known IDs and `PendingCaptureTicketNotReused == null`, set:

```csharp
operation.PendingCaptureTicketNotReused =
    !operation.CreatedPendingCaptureTicketIds.Overlaps(loadedIds);
```

Only update cancellations whose `CompletedUtc <= successful.StartedUtc`.

- [x] **Step 6: Verify Gate C**

Run:

```bash
../../../work/dotnet/dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~EvidenceSessionRecorderTests|FullyQualifiedName~EvidenceAssessmentTests" --verbosity minimal
```

Expected: known disjoint follow-up produces Gate C PASS; intersection produces FAIL; unknown remains INCOMPLETE.

---

### Task 4: WinForms Orchestration

**Files:**
- Modify: `src/Wx411.Easy/MainForm.cs`
- Test: `tests/Wx411.Core.Tests/MainFormEvidenceContractTests.cs`

**Interfaces:**
- Consumes: `PendingCaptureVault.SnapshotRecordIds`, both Task 3 recorder APIs, and `CaptureRecoveryResult.LoadedPendingCaptureTicketIds`.
- Produces: no new UI controls; existing precise-capture workflow gains evidence correlation.

- [x] **Step 1: Write failing source-contract tests**

Require:

```csharp
Assert.Contains("private readonly PendingCaptureVault _pendingCaptureVault;", source);
Assert.Contains("pendingTicketIdsBefore = TrySnapshotPendingCaptureTicketIds()", capture);
Assert.Contains("RecordCancelledPendingTickets", capture);
Assert.Contains("RecordPendingCaptureFollowUp", capture);
Assert.Contains("result.LoadedPendingCaptureTicketIds", capture);
```

Assert cancellation registration occurs after `CompleteEvidenceOperationAsync`, and follow-up confirmation occurs only in the success path.

- [x] **Step 2: Verify RED**

Run:

```bash
../../../work/dotnet/dotnet test tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --no-restore --filter FullyQualifiedName~MainFormEvidenceContractTests --verbosity minimal
```

Expected: assertions fail against RC3 wiring.

- [x] **Step 3: Retain the vault and snapshot safely**

Assign the constructor-created vault to `_pendingCaptureVault`. Add a helper returning `IReadOnlyList<string>?`; catch metadata enumeration exceptions, append one evidence warning, and return `null`.

- [x] **Step 4: Wire cancellation evidence**

At precise-capture start, snapshot IDs. In the cancellation catch, after completing the evidence operation, snapshot again. If both snapshots exist, compute `after.Except(before, StringComparer.OrdinalIgnoreCase)` and register the detached array; otherwise register `null`.

- [x] **Step 5: Wire successful follow-up**

After the current precise operation is completed as success, call `RecordPendingCaptureFollowUp` with its handle and `result.LoadedPendingCaptureTicketIds`. Catch recorder errors and append a log without changing capture success.

- [x] **Step 6: Verify GREEN**

Run the Task 4 command, then explicitly build WinForms:

```bash
../../../work/dotnet/dotnet build src/Wx411.Easy/Wx411.Easy.csproj -c Release --no-restore -p:TreatWarningsAsErrors=true --verbosity minimal
```

Expected: contract tests pass; build reports 0 warnings and 0 errors.

---

### Task 5: Full Verification and RC4

**Files:**
- Create: `dist/refactor-rc4/TEST-STEPS.txt`
- Create: `dist/refactor-rc4/SHA256SUMS.txt`
- Create: `dist/refactor-rc4/Wx411Easy-v1.5-refactor-rc4.exe`
- Create: `dist/refactor-rc4/Wx411Easy-v1.5-refactor-rc4.zip`
- Modify: this plan, marking completed checkboxes.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: verified RC4 Windows x64 single-file release.

- [x] **Step 1: Run full Release tests**

```bash
../../../work/dotnet/dotnet test Wx411Easy.sln -c Release --no-restore -p:TreatWarningsAsErrors=true --verbosity minimal
```

Expected: 0 failed tests and 0 warnings.

- [x] **Step 2: Verify formatting**

```bash
../../../work/dotnet/dotnet format Wx411Easy.sln --verify-no-changes --no-restore --verbosity minimal
```

Expected: exit code 0.

- [x] **Step 3: Request independent review**

Review vault metadata privacy, loaded/new ID separation, chronological correlation, null failure behavior, and MainForm ordering. Fix all Critical and Important findings with TDD.

- [x] **Step 4: Publish RC4**

```bash
staging=$(mktemp -d "${TMPDIR:-/tmp}/wx411-rc4-publish.XXXXXX")
../../../work/dotnet/dotnet publish src/Wx411.Easy/Wx411.Easy.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:TreatWarningsAsErrors=true -o "$staging" --verbosity minimal
```

Expected: 0 warnings/errors; `file` identifies PE32+ GUI x86-64.

- [x] **Step 5: Write Chinese RC4 test steps**

State the exact trigger: cancel precise capture, keep RC4 open, run precise capture again to success, export evidence, and check Gate C. Explain PASS, FAIL, and INCOMPLETE outcomes.

- [x] **Step 6: Package and verify**

ZIP only the RC4 EXE and `TEST-STEPS.txt`; generate SHA-256 for EXE, ZIP, and test steps. Extract to a unique temporary directory and use `cmp` to verify byte identity. Re-check the RC3 ZIP hash remains `db45417d518d7bee5828b0c1eb36e9420362a40f85d13263298ffba0b67d0691`.
