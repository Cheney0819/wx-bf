# Recovery Worker Foundation Closure Report

Date: 2026-07-25  
Branch: `feature/recovery-worker-foundation`  
Worktree: `.worktrees/recovery-worker-foundation`

## Result

The Recovery foundation is complete as an isolated, non-cutover implementation. It imports the frozen RC9 Core, persists validated keys and irreversible restart accounting, reuses keys for later database generations, publishes immutable schema-v1 handoffs, and provides a long-running elevated Worker host.

The existing WPF startup path, `App.xaml.cs`, `WeChatMonitor.cs`, Python runtime, server, installer, and Android project were not changed. No old and new capture chains are started together by this phase.

## Source Provenance

- Frozen RC9 source ZIP SHA-256: `45bb644a0dce03331bc9c57901700d73e7976750b59ee03b20af50d7a3e40ebc`
- Frozen RC9 release ZIP SHA-256: `5d1d5e3b8159b439e044598fa9ae35c4d37a5d006bd2dd7c27e60f474da6713d`
- SQLCipher fixture SHA-256: `5b574c49e27eb3dcffcedebb0a122c3c7f735a8b8f6fb410c1ccc7477fcb271d`
- Non-default SQLCipher fixture SHA-256: `3fece48c3efdb1c0c7cfefbafffa72bd228be2d0dcac96856a05cbfe3f072922`
- .NET SDK used: `8.0.423`

Integration deltas are enumerated in `windows-background/IMPORTED-RC9.md`. They are limited to test-fixture location, the RC9 documentation identity assertion, the validated-key sink, its tests, and comments that explain pre-existing best-effort cleanup catches.

## Automated Gate

Fresh commands executed from the isolated worktree:

```text
dotnet test windows-background/DesktopPet.Background.sln --no-restore
dotnet build windows-background/DesktopPet.Background.sln -c Release --no-restore -p:EnableWindowsTargeting=true
python3 -m pytest -q tests
git diff --check
```

Results:

- `Wx411.Core.Tests`: 210 passed, 0 failed, 0 skipped
- `DesktopPet.Background.Tests`: 4 passed, 0 failed, 0 skipped
- `DesktopPet.Recovery.Tests`: 70 passed, 0 failed, 0 skipped
- Existing Python contract tests: 15 passed
- Release build: 0 warnings, 0 errors
- Diff whitespace gate: clean
- Credential/endpoint scan in `windows-background`: no server token, upload endpoint, or URL
- Empty-catch scan in production source: no unexplained empty catches

The framework-dependent Worker Release directory is approximately 2.2 MB on the macOS build host. The Worker assembly is 49 KB. This is not a Windows distribution archive, but it confirms the foundation did not introduce another self-contained 100+ MB payload.

## State And Failure Matrix

Automated coverage proves:

- the current process is capture attempt zero;
- only an initiated restart consumes budget;
- restart consumption is saved to the critical snapshot before SQLite mutation;
- exactly two restarts are available per epoch and survive repository/Worker reopen;
- a restart exception never refunds budget, including the next Worker run;
- validated key and pending capture suppress all later active restarts;
- an exhausted epoch opens a persistent circuit;
- a later natural process start may still perform one passive capture while the circuit remains closed to restarts;
- database-change and five-minute reconciliation triggers run persisted-key reuse only and do not attach or restart;
- stored keys decrypt real SQLCipher fixtures without capture;
- one database failure does not block another database generation;
- completed generations are idempotent across Worker runs;
- RC9 exceptions are mapped to stable codes without parsing localized text;
- a pending ticket observed after an RC9 exception suppresses restart;
- handoff generation IDs derive from epoch, normalized relative path, and plaintext SHA-256;
- handoff files and manifests are immutable, atomic, idempotent, hash-checked, and path-contained;
- Windows-style traversal, drive paths, UNC paths, rooted paths, and external staging sources are rejected;
- runtime diagnostics retain only the newest 200 sanitized events;
- missing SQLite state is restored from the critical snapshot;
- corrupt SQLite state without a critical snapshot stops Recovery instead of resetting restart budget;
- `--diagnose` uses read-only SQLite and emits one sanitized JSON object, including for missing or corrupt state;
- duplicate Worker ownership is rejected and the process watcher emits only newly observed PIDs;
- known-root file watching never scans a whole drive;
- hint-source and cycle failures cancel companion background tasks promptly.

## Runtime Contract

`DesktopPet.Recovery.Worker` supports only:

- no arguments: long-running mode;
- `--once`: one bounded startup cycle;
- `--diagnose`: one read-only JSON diagnostic object.

The Worker owns no server URL or token and performs no upload. Its named mutex is `Local\JunjieeDesktopPet-Recovery-v1`. The manifest uses `asInvoker`; highest privileges will be supplied later by the scheduled-task installer rather than by an interactive UAC prompt.

The handoff contract is `DatabaseReadyManifest` schema 1:

```text
schemaVersion, manifestId, epochId, createdAtUtc,
databases[] { generationId, relativePath, plaintextPath, sha256 }
```

Ready manifests are written under `Background/Handoff/ready`; immutable readable databases are under `Background/Recovery/Generations`.

## Windows-Only Acceptance Still Required

The following paths compiled but were not executed on the macOS build host:

- Windows DPAPI protection and recovery under the scheduled-task account;
- Authenticode signer identity extraction from the real target executable;
- RC9 debug attach, early-bird module load observation, and real key capture;
- same-session process-tree termination and restart of the snapshotted executable;
- Windows `FileSystemWatcher` behavior under sustained live database writes;
- the generated `.exe` application manifest and highest-privilege scheduled task;
- persistence across Windows logoff, reboot, desktop-pet exit, and Worker crash;
- real handoff consumption by DataSync.

These are deliberately deferred to one consolidated Windows acceptance pass after DataSync consumes a real schema-v1 handoff fixture. Installer cutover must not begin before that gate passes.

## Commits

- `c5e8106` `build: import frozen rc9 core`
- `7c9277d` `feat: add background contracts and paths`
- `35ef6d8` `feat: persist recovery epochs and restart budget`
- `5c312da` `feat: persist validated database keys`
- `12035ed` `feat: add bounded capture state machine`
- `15b4a8e` `feat: reuse keys for database generations`
- `d36c678` `feat: publish atomic database handoffs`
- `8293a1e` `feat: coordinate rc9 recovery attempts`
- `827df7d` `feat: add persistent recovery worker host`
