# DataSync And Parser Worker Foundation Closure Report

Date: 2026-07-26
Branch: `feature/recovery-worker-foundation`
Worktree: `.worktrees/recovery-worker-foundation`

## Result

The DataSync and pure Parser foundation is complete as an isolated, non-cutover implementation. A normal-privilege DataSync Worker imports immutable Recovery handoffs, persists parser and upload work in its own SQLite database, runs one bounded Parser job at a time, commits incremental identities and encrypted Outbox rows atomically, and uploads with two concurrent durable leases.

Recovery remains the sole owner of process access, keys, restarts, decryption, and readable database generations. The Parser receives only a controlled job manifest and readable database copies. It contains no process discovery, memory access, key handling, restart, server, token, or network code.

The existing WPF startup path, `App.xaml.cs`, `WeChatMonitor.cs`, `wx_decrypt.py`, installer projects, server, and Android project were not changed. The old and new runtime chains are not started together by this phase.

## Automated Gate

Fresh commands executed from the isolated worktree:

```text
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter EndToEndDataSyncTests --no-restore
python3 -m pytest -q windows-parser/tests
python3 -m pytest -q tests
dotnet test windows-background/DesktopPet.Background.sln --no-restore
dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true --no-restore
git diff --check
```

Results:

- `Wx411.Core.Tests`: 210 passed, 0 failed, 0 skipped
- `DesktopPet.Recovery.Tests`: 70 passed, 0 failed, 0 skipped
- `DesktopPet.DataSync.Tests`: 82 passed, 0 failed, 0 skipped
- `DesktopPet.Background.Tests`: 4 passed, 0 failed, 0 skipped
- Full .NET solution: 366 passed, 0 failed, 0 skipped
- Parser Python tests: 10 passed
- Existing Python contract tests: 15 passed
- Release build: 0 warnings, 0 errors
- Diff whitespace gate: clean
- Recovery server-setting scan: no server URL, token, authorization header, or HTTP client
- DataSync and Parser production catch scan: no unexplained empty catches
- .NET SDK used: `8.0.423`

The end-to-end test ran the production Recovery publisher, handoff importer, controlled Parser process, atomic incremental commit, encrypted Outbox, and loopback HTTP uploader. The TRX result records 0.951 seconds for the test body.

## Real Handoff Evidence

The test copies the frozen readable SQLite files into Recovery staging, verifies their hashes, and calls `AtomicHandoffPublisher.PublishAsync`. Generation and manifest IDs are therefore produced by production code rather than hand-authored in the fixture.

```text
e0f16e5fd109b5b64fd3f47c9293783f1a324bf4a4ac5475c17e511b3a8ad1e9  readable-set/contact/contact.db
2b93b4f05185f2040f22ca01d75ce30001c775a77e321323d8327297305a385a  readable-set/favorite/favorite.db
3f39e7b3881d348943ea50b9953d4081738f15382f81d15ce9b4dc6ba6ba1e18  readable-set/message/message_0.db
```

The completed run proves:

- one accepted handoff marker;
- one completed parse job and no duplicate second job;
- five stable exported identities;
- three encrypted Outbox rows;
- five physical HTTP attempts across repeated repository/uploader reopen;
- transient response sequence `500, 429, 200` followed by success for remaining work;
- all three logical Outbox rows end in `acknowledged` state;
- retries preserve exactly one body per logical request ID.

The exact endpoint/request identities are:

```text
/api/contacts   b7331b6cf9f1600d2174664a8936abf600e6c6d3659a7a22414da12b07fee2b7
/api/favorites  61a012c5e5247e99921967306e92bc585e8c831abd530bbbba71c36811436417
/api/messages   d7082e5f7d2aef0aa78fbd6e9a15a409a1394e830845842769cac8fc8db9f32d
```

## Runtime Contract

`DesktopPet.DataSync.Worker` supports:

- no arguments: long-running handoff, parser, and upload loops;
- `--once`: one bounded reconciliation cycle;
- `--diagnose`: one read-only sanitized status object.

Runtime limits are deliberately bounded:

- named mutex: `Local\JunjieeDesktopPet-DataSync-v1`;
- parser concurrency: 1;
- upload concurrency: 2;
- ready-manifest debounce: 2 seconds;
- full reconciliation: 5 minutes;
- upload polling: 15 seconds;
- parser soft timeout: 120 seconds;
- parser hard timeout: 180 seconds with process-tree termination;
- parser stdout: one schema-v1 completion document;
- parser stderr: capped at 64 KiB.

Server settings live only in the normal-privilege DataSync boundary and use current-user DPAPI in production. Outbox payloads are protected before SQLite insertion. Parser result validation, stable item insertion, Outbox insertion, and parse completion share one transaction.

## Packaging Evidence

`windows-parser/build-parser.ps1` builds a clean PyInstaller `--onedir` Parser, publishes both Workers framework-dependent, hash-merges shared Worker files, omits `.pdb` and `.xml`, writes the relative Parser install manifest, produces a ZIP, and fails above 85 MiB.

A local macOS proxy build was produced with Python `3.13.8` and PyInstaller `6.11.1`:

```text
Parser executable SHA-256: 64fd4776aac45cb1625084e6818bcaa15b7517b095ad9b5aa85943190e671b5c
Parser onedir disk use:    21,188 KiB
Merged package disk use:   49,648 KiB
Merged file bytes:         50,605,826
Compressed ZIP bytes:      22,705,340 (21.65 MiB)
```

This proxy conservatively includes seven Worker PDB files that the Windows script excludes. It demonstrates substantial margin below 85 MiB, but it is not a Windows release artifact and its Parser hash is not the final Windows hash.

## Windows-Only Acceptance Still Required

The following checks are intentionally consolidated into one Windows run:

- execute `windows-parser/build-parser.ps1` and record the authoritative Windows Parser hash, uncompressed bytes, ZIP bytes, and 85 MiB gate result;
- run the real packaged `wx_parser.exe` through the handoff-to-loopback-server E2E path;
- verify current-user DPAPI settings and encrypted Outbox reopen under the scheduled-task account;
- verify the normal-privilege DataSync Worker remains alive across desktop-pet exit, logoff/restart, Worker crash, and handoff arrival;
- verify Windows `FileSystemWatcher`, parser cancellation, and process-tree hard termination behavior;
- verify Recovery and DataSync scheduled-task identities and privilege separation.

Installer and WPF cutover must remain separate until this consolidated gate passes. External image-byte extraction is also a separate pre-cutover phase; the foundation currently carries its validated metadata contract only.

## Commits

- `babe551` `feat: add durable datasync state and encrypted outbox`
- `933cae4` `feat: import recovery handoffs idempotently`
- `5d9c95d` `feat: extract pure database parser`
- `0cf23f8` `feat: supervise bounded parser jobs`
- `21c2e16` `feat: commit incremental parser results to outbox`
- `c44532f` `feat: upload encrypted outbox durably`
- `f53cedd` `feat: add persistent datasync worker host`

The Task 8 closure commit contains the end-to-end fixture, package gate, relative Parser install-path support, and this report.
