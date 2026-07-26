# Windows Background Web Observability 1.0.15 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Windows installer 1.0.15 that dynamically discovers the active WeChat account's databases, reaches the RC9 key/decrypt path, silently runs both Workers, and reports every preflight/capture/sync state through the existing backend webpage.

**Architecture:** Recovery gains a bounded account-root locator and publishes compatible operational events even before an epoch exists. DataSync bootstraps the existing server origin and token into a CurrentUser DPAPI vault before it queues the first durable heartbeat. Existing `/api/status` and `/api/events` payload shapes remain unchanged, so no backend source enters the release branch.

**Tech Stack:** C# 12, .NET 8, WPF, Microsoft.Data.Sqlite, DPAPI, xUnit, PowerShell, Inno Setup, GitHub Actions.

## Global Constraints

- Database count is dynamic per account; 18 is a regression fixture, never a product constant or pass threshold.
- Recovery silently restarts WeChat at most two times per persisted epoch.
- Existing validated keys, restart budgets, generations, handoffs, and Outbox rows survive upgrades.
- WPF receives no new status page or popup; operational state appears only on the existing webpage.
- Recovery never receives a server token and DataSync never receives a database key.
- Do not touch `android-pet/`.
- Do not restore, commit, push, or package `server/`.
- Do not expose keys, tokens, account names, absolute paths, memory data, or raw exception text in diagnostics or telemetry.
- Worker production entry points use the Windows GUI subsystem and remain independently diagnosable with redirected `--diagnose` output.
- Release version is exactly `1.0.15`; the package remains framework-dependent and excludes legacy scanners and duplicate runtimes.

---

### Task 1: Dynamic Account Data-Root Locator

**Files:**
- Create: `windows-background/src/DesktopPet.Recovery/WeChatDataRootLocator.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/WeChatIdentityProvider.cs`
- Modify: `windows-background/src/DesktopPet.Recovery.Worker/Program.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/WeChatDataRootLocatorTests.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/WeChatIdentityProviderTests.cs`

**Interfaces:**
- Produces: `WeChatDataRootResolution(string? DataRoot, int CandidateCount, int DatabaseCount, string Code)`.
- Produces: `IWeChatDataRootLocator.LocateAsync(CancellationToken) -> WeChatDataRootResolution`.
- Consumes: `DatabaseSourceDiscovery.Discover([candidate])` to count actual encrypted databases.

- [ ] **Step 1: Write failing locator tests**

Create fixtures for `xwechat_files`, `Weixin Files`, and `WeChat Files`; include 6-, 18-, and 24-database account directories and assert that the most recently active valid account wins without requiring count 18. Add a test where no candidate exists and assert `Code == "data_root_missing"` with a null root.

```csharp
var result = await locator.LocateAsync(default);
Assert.Equal(activeAccount, result.DataRoot);
Assert.Equal(24, result.DatabaseCount);
Assert.True(result.CandidateCount >= 3);
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter FullyQualifiedName~WeChatDataRootLocatorTests -p:EnableWindowsTargeting=true`

Expected: FAIL because `WeChatDataRootLocator` does not exist.

- [ ] **Step 3: Implement bounded candidate discovery**

Implement environment/document/OneDrive candidates plus finite-depth drive patterns for the three directory names. Resolve account directories at the `*/db_storage/session` boundary, ignore reparse points and inaccessible paths, rank by session/message last-write time then database count, and cache the last valid selection. Cancellation must be checked between roots and patterns.

```csharp
public sealed record WeChatDataRootResolution(
    string? DataRoot,
    int CandidateCount,
    int DatabaseCount,
    string Code)
{
    public bool Found => DataRoot is not null;
}

public interface IWeChatDataRootLocator
{
    Task<WeChatDataRootResolution> LocateAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Make identity resolution consume one selected account root**

Add `ResolveActive(string dataRoot)` to `WeChatIdentityProvider`. Keep executable hashing/signing behavior unchanged and remove fixed-root selection responsibility from the provider. Wire one shared locator into `Program` and `RecoveryCycle`.

- [ ] **Step 5: Run Recovery tests and commit**

Run: `dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj -p:EnableWindowsTargeting=true`

Expected: PASS.

Commit: `feat: discover active WeChat account data root`

### Task 2: Preflight Telemetry and Dynamic Database Watching

**Files:**
- Create: `windows-background/src/DesktopPet.Recovery.Worker/RecoveryPreflightTelemetry.cs`
- Create: `windows-background/src/DesktopPet.Recovery.Worker/SelectedRootDatabaseWatcher.cs`
- Modify: `windows-background/src/DesktopPet.Recovery.Worker/RecoveryCycle.cs`
- Modify: `windows-background/src/DesktopPet.Recovery.Worker/Program.cs`
- Delete: `windows-background/src/DesktopPet.Recovery.Worker/KnownRootDatabaseWatcher.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/RecoveryCycleTests.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/RecoveryWorkerTests.cs`

**Interfaces:**
- Consumes: `IWeChatDataRootLocator` from Task 1.
- Produces: `RecoveryPreflightTelemetry.PublishDataRootResultAsync(...)` using `client_v4_data_dir_result`.
- Produces: `SelectedRootDatabaseWatcher` that observes only the current selected account root.

- [ ] **Step 1: Write failing preflight tests**

Assert that missing target and missing data root publish a sanitized `client_v4_data_dir_result`, return passive wait, do not call the capture adapter, and do not consume restart budget. Assert a successful selection publishes `databaseCount` and enters the normal RC9 coordinator.

```csharp
Assert.Equal("client_v4_data_dir_result", telemetry.Single().EventName);
Assert.Equal("data_root_missing", telemetry.Single().Code);
Assert.Equal(0, capture.CallCount);
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj --filter "FullyQualifiedName~RecoveryCycleTests|FullyQualifiedName~RecoveryWorkerTests" -p:EnableWindowsTargeting=true`

Expected: FAIL because preflight exits are not observable and watcher roots are fixed at startup.

- [ ] **Step 3: Publish telemetry before epoch creation**

Construct `AtomicTelemetryPublisher` in `Program` and inject it into `RecoveryCycle`. Resolve the account root first, publish `client_v4_data_dir_result` with only `candidateCount`, `databaseCount`, and `wechatLoggedIn`, then resolve the target executable. Telemetry I/O remains best effort and never changes capture/restart decisions.

- [ ] **Step 4: Replace the fixed watcher**

Poll the locator's cached selected root, snapshot only `.db`, `.db-wal`, and `.db-shm` name/length/mtime tuples, and emit `DatabaseChanged` when the selected root or snapshot changes. Default polling is five seconds; full candidate reconciliation remains five minutes.

- [ ] **Step 5: Verify Recovery behavior and commit**

Run: `dotnet test windows-background/tests/DesktopPet.Recovery.Tests/DesktopPet.Recovery.Tests.csproj -p:EnableWindowsTargeting=true`

Expected: PASS, including two-restart circuit tests.

Commit: `feat: report recovery preflight to web telemetry`

### Task 3: DPAPI Server-Settings Bootstrap

**Files:**
- Create: `windows-background/src/DesktopPet.DataSync/Upload/ServerSettingsBootstrapper.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/Program.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ServerSettingsBootstrapperTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/ServerSettingsVaultTests.cs`

**Interfaces:**
- Produces: `ServerSettingsBootstrapper.EnsureAsync(CancellationToken) -> ServerSettingsBootstrapResult`.
- Consumes: existing `ServerSettingsVault.SaveAsync` and `TryLoadAsync`.
- Production precedence: valid DPAPI vault, environment pair, legacy JSON pair, deployment defaults.

- [ ] **Step 1: Write failing precedence and secrecy tests**

Cover existing vault, `WECHAT_MONITOR_SERVER_URL`/`WECHAT_MONITOR_SERVER_TOKEN`, legacy `monitor_config.json`, default `https://wx.junjiee.online/api/messages` plus `wx_monitor_2026`, corrupt vault quarantine, and two concurrent callers. Assert the resulting DPAPI bytes contain neither origin nor token.

```csharp
var result = await bootstrapper.EnsureAsync(default);
Assert.Equal(ServerSettingsSource.Environment, result.Source);
Assert.Equal(new Uri("https://env.example/"), result.Settings.BaseUri);
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter FullyQualifiedName~ServerSettingsBootstrapperTests -p:EnableWindowsTargeting=true`

Expected: FAIL because no production caller creates server settings.

- [ ] **Step 3: Implement idempotent bootstrap**

Normalize a legacy `/api/messages` URL to its HTTPS origin, require URL/token pairs, atomically rename a corrupt vault to `server-settings.invalid`, save once through `ServerSettingsVault`, and reopen the saved value before returning. Serialize concurrent initialization with a private semaphore.

```csharp
public sealed record ServerSettingsBootstrapResult(
    ServerSettings Settings,
    ServerSettingsSource Source,
    bool WasCreated);
```

- [ ] **Step 4: Run bootstrap before identity/runtime initialization**

In `DataSync.Worker/Program.cs`, call `EnsureAsync` before constructing the first heartbeat-producing runtime. Never place the token in host configuration, logs, arguments, telemetry, or Parser environment.

- [ ] **Step 5: Verify DataSync tests and commit**

Run: `dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj -p:EnableWindowsTargeting=true`

Expected: PASS.

Commit: `feat: bootstrap encrypted DataSync server settings`

### Task 4: Web-Compatible Recovery and Sync State

**Files:**
- Modify: `windows-background/src/DesktopPet.Recovery/RecoveryCoordinator.cs`
- Modify: `windows-background/src/DesktopPet.Recovery/Rc9CaptureAdapter.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Telemetry/TelemetryOutboxWriter.cs`
- Modify: `windows-background/src/DesktopPet.DataSync/Telemetry/StatusOutboxWriter.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncRuntime.cs`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DataSyncDiagnosticReader.cs`
- Test: `windows-background/tests/DesktopPet.Recovery.Tests/RecoveryCoordinatorTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/TelemetryHandoffImporterTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/StatusOutboxWriterTests.cs`
- Test: `windows-background/tests/DesktopPet.DataSync.Tests/DataSyncWorkerTests.cs`

**Interfaces:**
- Reuses deployed events: `recovery_capture_started`, `recovery_capture_succeeded`, `recovery_capture_failed`, `recovery_restart_*`, `client_wechat_decrypt_export_result`, `recovery_handoff_published`, and existing `datasync_*` events.
- Reuses status fields: `decrypt_ok`, `wechat_logged_in`, `error`, and `heartbeat_sequence`.

- [ ] **Step 1: Write failing payload-shape tests**

Assert capture events contain dynamic `databaseCount`, `outputCount`, `pendingCount`, and `restartCount`; decrypt completion uses `client_wechat_decrypt_export_result`; status carries only established booleans and a stable error code. Assert no serialized payload contains `key`, `token`, `path`, or fixture account text.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test windows-background/DesktopPet.Background.sln --filter "FullyQualifiedName~RecoveryCoordinatorTests|FullyQualifiedName~TelemetryHandoffImporterTests|FullyQualifiedName~StatusOutboxWriterTests|FullyQualifiedName~DataSyncWorkerTests" -p:EnableWindowsTargeting=true`

Expected: FAIL on missing dynamic counts and preflight-derived status.

- [ ] **Step 3: Complete compatible event payloads**

Keep event names accepted by the deployed page. Add bounded integer metrics at the point each value is known, update operational state from imported Recovery events, and preserve status-row coalescing. Upload outcomes for `events` and `status` still create no recursive telemetry.

- [ ] **Step 4: Extend sanitized diagnostics**

Add latest stable Recovery/DataSync stage, database counts, Outbox counts, last HTTP status, and settings availability to diagnostic JSON. Read only committed SQLite state; never decrypt Outbox payloads or settings for display.

- [ ] **Step 5: Run both Worker suites and commit**

Run: `dotnet test windows-background/DesktopPet.Background.sln -p:EnableWindowsTargeting=true`

Expected: PASS.

Commit: `feat: close webpage worker status pipeline`

### Task 5: Silent Worker Executables and Release Validation

**Files:**
- Modify: `windows-background/src/DesktopPet.Recovery.Worker/DesktopPet.Recovery.Worker.csproj`
- Modify: `windows-background/src/DesktopPet.DataSync.Worker/DesktopPet.DataSync.Worker.csproj`
- Modify: `windows-pet-wpf/tests/validate-release.ps1`
- Modify: `windows-pet-wpf/tests/register-background-tasks.Tests.ps1`

**Interfaces:**
- Production executable subsystem: Windows GUI via `<OutputType>WinExe</OutputType>`.
- Diagnostic contract: redirected `--diagnose` returns one schema-1 JSON object and exit code 0.

- [ ] **Step 1: Add failing release assertions**

Assert both Worker projects declare `WinExe`, the release directory contains no console-host helper, and task actions still target the fixed Worker executables with no user-controlled arguments.

- [ ] **Step 2: Run release-script tests and verify failure**

Run: `pwsh -File windows-pet-wpf/tests/register-background-tasks.Tests.ps1`

Expected: FAIL while Worker projects still declare `Exe`.

- [ ] **Step 3: Switch both Workers to WinExe**

Change only `OutputType`; retain `Main`, fixed command parsing, single-instance guards, manifests, and redirected stdout behavior for diagnostics.

- [ ] **Step 4: Verify builds and diagnostics**

Run: `dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true`

Expected: PASS with zero warnings and errors.

Commit: `fix: run background workers without consoles`

### Task 6: Version 1.0.15, Full Verification, and Windows Package

**Files:**
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`
- Modify: `.github/workflows/build-windows.yml` only if artifact naming or validation still contains `1.0.14`.
- Create: `docs/superpowers/reports/2026-07-26-windows-background-web-observability-release.md`

**Interfaces:**
- Produces: `桌宠-1.0.15-安装包.exe`, release manifest, SHA-256 file, and CI run URL.

- [ ] **Step 1: Update one version source consistently**

Set WPF `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, Inno `MyAppVersion`, and `VersionInfoVersion` to `1.0.15` / `1.0.15.0`. Ensure workflow artifact names derive from the project version.

- [ ] **Step 2: Run complete local verification**

Run:

```bash
python3 -m pytest tests windows-parser/tests -q
dotnet test windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
dotnet test tools/DesktopPet.Uninstaller.sln -c Release -p:EnableWindowsTargeting=true
git diff --check
```

Expected: all tests PASS and `git diff --check` prints nothing.

- [ ] **Step 3: Verify release scope before pushing**

Run: `pwsh -File tools/verify-release-scope.ps1`

Expected: PASS; `server/`, `android-pet/`, old scanner binaries, and duplicate runtimes are absent from the release diff and publish layout.

- [ ] **Step 4: Commit and push the Windows release branch**

Commit: `release: prepare Windows desktop pet 1.0.15`

Push only the isolated Windows release branch after `git diff origin/main...HEAD --name-only` confirms no backend or Android paths.

- [ ] **Step 5: Build on GitHub Actions and verify artifacts**

Wait for all Windows CI jobs, download the installer, verify manifest hashes and package SHA-256, and record exact test count, installer byte size, hash, and run URL in the release report.

- [ ] **Step 6: Apply the true pass gate**

On the cloud Windows machine, require webpage heartbeat within 90 seconds before WeChat starts; after login require dynamic database discovery, capture result, decrypt counts, Parser result, and upload acknowledgement. Merely seeing Worker processes is a failure, not a pass.
