# DataSync Telemetry And Backend Contract Closure Report

Date: 2026-07-26
Branch: `feature/recovery-worker-foundation`
Task 6 base: `cfd92872dbe530c27714e81d0913e34cce44d561`

## Result

The telemetry/backend contract is closed by tests and documentation without a production-code change in Task 6. One end-to-end run now uses the production Recovery telemetry publisher, Recovery database handoff publisher, DataSync legacy identity store, telemetry importer, status writer, Parser process boundary, encrypted Outbox, durable uploader, and a loopback receipt backend.

The imported legacy identity is `client-cs-existing` / `client_cs`. Messages, contacts, favorites, events, and status all carry that same identity before encryption, after decryption, and at the HTTP boundary. Reopening the identity store returns the same persisted document.

## Cross-Contract Evidence

The expanded `EndToEndDataSyncTests` proves:

- production `AtomicTelemetryPublisher` creates a sanitized atomic Recovery event whose filename matches its 64-hex event ID;
- DataSync imports and removes that ready file, persists its identity, and creates one encrypted `events` row;
- the real Recovery readable-database handoff still produces five exported business identities and three business Outbox rows;
- two heartbeats leave one newer `status` row, with the first row physically removed;
- the final Outbox contains exactly five logical rows: messages, contacts, favorites, events, and status;
- eight physical HTTP attempts cover two transient retries, five logical successes, and one response-loss replay;
- the first three backend outcomes remain exactly `500, 429, 200`;
- the first successful event request is committed and cached, then its socket closes before a response; a reopened repository/uploader retries the identical body and `request_id`, receives cached success, and acknowledges the row;
- the loopback backend records five logical effects, each exactly once, including one logical Recovery event;
- all five rows finish `acknowledged`, while exported-item and Outbox counts remain five.

`server/tests/test_api_contract.py` exercises all five Flask POST handlers with the real imported `claim_request` and `complete_request` functions over a deterministic fake transactional connection. It proves each receipt and route response preserves the same session/source. For events, the first committed response is deliberately discarded; replay of the same request returns the cached JSON, performs no second commit, and records `recovery_capture_succeeded` once.

## TDD And Review

The first closure assertion was run before the E2E fixture was extended:

```text
dotnet test windows-background/tests/DesktopPet.DataSync.Tests/DesktopPet.DataSync.Tests.csproj --filter EndToEndDataSyncTests --no-restore
Failed: 1, Passed: 0, Total: 1
Expected endpoint families: contacts, events, favorites, messages, status
Actual endpoint families:   contacts, favorites, messages
```

After wiring the existing production identity, telemetry, and status components, the focused E2E passed `1/1`. The new backend route contract passed `2/2` on its first run because Task 2 already supplied the required production idempotency; no backend source change was justified.

Independent review initially found that the E2E hand-authored its telemetry JSON and did not prove that the newer heartbeat survived coalescing. The test was changed to use production `AtomicTelemetryPublisher`, capture the first heartbeat ID, require a different surviving ID, and require the first row to be absent. Focused tests remained green. Re-review reported no Critical, Important, or Minor findings.

## Final Gates

Fresh commands were executed from the linked worktree with .NET SDK `8.0.423` available through `~/.dotnet`:

```text
python3 -m pytest -q server/tests
31 passed in 0.11s

python3 -m pytest -q windows-parser/tests
10 passed in 0.41s

python3 -m pytest -q tests
15 passed in 0.11s

dotnet test windows-background/DesktopPet.Background.sln
DesktopPet.DataSync.Tests: 124 passed
DesktopPet.Background.Tests: 4 passed
Wx411.Core.Tests: 210 passed
DesktopPet.Recovery.Tests: 91 passed
Full solution: 429 passed, 0 failed, 0 skipped

dotnet build windows-background/DesktopPet.Background.sln -c Release -p:EnableWindowsTargeting=true
Build succeeded: 0 warnings, 0 errors; elapsed 00:00:02.27

python3 -m py_compile server/server.py server/request_receipts.py
exit 0

git diff --check
exit 0
```

## Boundary Audit

- Task 6 changes are limited to the E2E test, backend contract test, plan/checklists, and reports. No production source changed.
- Across the complete design range from `dabc922`, no WPF startup, installer, Android, `WeChatMonitor.cs`, `wx_decrypt.py`, or Parser source file changed.
- Recovery production scans contain no server settings, URL/host configuration, token, authorization header, or HTTP client. Recovery publishes local files only.
- Parser production scans contain no client/session/request identity, credential, token, server, URL, socket, `requests`, or `urllib` capability.
- DataSync alone owns the imported identity, settings, encryption, status/event Outbox, and HTTP upload.
- Upload outcomes can create events only for messages, contacts, and favorites. Events and status cannot recursively create new event rows.
- Status coalescing deletes only unleased pending rows for the exact session prefix. Offline operation retains at most one replaceable pending heartbeat per session in addition to a currently leased row; leased rows remain recoverable.
- No empty catch was added in Task 6. Existing catches were audited: Recovery telemetry ignores only temporary-file cleanup failures; DataSync's heartbeat wrapper is explicitly best effort after the runtime records bounded diagnostics; Parser ignores only absent temporary files and unsupported directory `fsync`; server `pass` blocks are pre-existing idempotent `ALTER TABLE`/index migration probes. No unexplained empty production catch remains in this design range.

The type chain remains consistent: C# `SessionId`, `EventId`, and request IDs serialize as `session_id`, `event_id`, and `request_id`; `source` is the client source at the payload and Flask boundary; SQLite and MySQL receipt identities retain the same meanings.

## Build And Package Facts

The fresh build used .NET SDK `8.0.423` on macOS arm64 and Python `3.13.8`. `EnableWindowsTargeting=true` verifies the complete Release project graph on this host, but it does not produce an authoritative Windows deployment package.

No packaging or installer script changed in this design range. The previously recorded macOS proxy package facts remain: Python `3.13.8`, PyInstaller `6.11.1`, Parser executable SHA-256 `64fd4776aac45cb1625084e6818bcaa15b7517b095ad9b5aa85943190e671b5c`, Parser onedir `21,188 KiB`, merged package `49,648 KiB` / `50,605,826` file bytes, and ZIP `22,705,340` bytes (`21.65 MiB`). Those facts were not regenerated by Task 6 and are not a final Windows artifact.

## Formatting Baseline

Solution-wide `dotnet format --verify-no-changes --no-restore` still reports nine pre-existing whitespace diagnostics in untouched `windows-background/tests/DesktopPet.DataSync.Tests/ParserResultTestData.cs` at lines 12-16, 21, 35, 49, and 50. Task 6 did not modify that file. The binding whitespace gate, `git diff --check`, is clean.

## Deferred Deployment Smoke

This macOS host has neither a `mysql` executable nor Docker. The live concurrent MySQL `SELECT ... FOR UPDATE`/duplicate-key locking smoke test did not run and is explicitly deferred to the Windows/server deployment environment. Local backend tests use deterministic fake transaction objects; they do not claim live MySQL integration.

The authoritative Windows packaging run, scheduled-account DPAPI/reopen check, and installed-worker lifecycle smoke also remain deployment gates. No functional concern is known in the locally exercised contract.

## Final-Review Remediation Addendum

The final branch review was remediated on 2026-07-26 without changing the
original Task 6 evidence above. Ambiguous exported identity joins are replaced
by versioned canonical JSON tuples for messages, contacts, favorites, and
batches. The resulting one-time re-export uses new client-side keys while
leaving backend business-key deduplication unchanged.

Recovery operational state now records envelope occurrence time and updates
only for an equal or newer event. Telemetry backlog reconciliation rejects
invalid envelopes independently and imports valid envelopes by occurrence time
with an event-ID tie-breaker. Reverse arrival and reverse filename tests prove
an older capture failure cannot overwrite a newer success.

DataSync schema 3 adds a durable per-client heartbeat counter. Allocation is a
separate immediate SQLite transaction, so a crash may leave a gap but cannot
reuse a sequence. Pending heartbeat coalescing still leaves leased rows
untouched. The backend stores internal heartbeat sequences on global and
session status rows; sequenced status writes update state only after the
session upsert accepts a newer value. Stale requests still complete their
receipt transaction and return the unchanged response shape. Requests without
the sequence retain legacy behavior.

Parser cleanup now has an explicit two-second default grace covering process
exit and both redirected stream readers on every exit path. A deterministic completion
waiter test produces the stable `parser_cleanup_timeout` terminal exception,
the runtime contains that exception as a bounded parser failure, and retained
descendant pipes cannot hold the operation open. The real process-tree test
remains green. Backend migrations now suppress
only MySQL `1060` for duplicate columns, `1061` for duplicate keys/indexes, and
`1091` for scoped idempotent index drops. Request IDs are hashed exactly as
accepted and leading/trailing whitespace is rejected across every POST route.

Fresh local closure evidence after remediation:

```text
python3 -m pytest -q server/tests
50 passed in 0.09s

python3 -m pytest -q windows-parser/tests
10 passed in 0.51s

python3 -m pytest -q tests
15 passed in 0.15s

~/.dotnet/dotnet test windows-background/DesktopPet.Background.sln --no-restore
DataSync 132, Background 4, Core 210, Recovery 91
437 passed, 0 failed, 0 skipped

~/.dotnet/dotnet build windows-background/DesktopPet.Background.sln \
  -c Release -p:EnableWindowsTargeting=true --no-restore
Build succeeded: 0 warnings, 0 errors; elapsed 00:00:01.23

python3 -m py_compile server/server.py server/request_receipts.py
exit 0

git diff --check
exit 0
```

The final-review diff does not touch Android, WPF startup, installer files,
Recovery production ownership, Parser Python source, `WeChatMonitor.cs`, or
`wx_decrypt.py`. Live MySQL/Docker concurrency and migration smoke tests remain
deferred because neither service is available on this host. Authoritative
Windows packaging, scheduled-account DPAPI/reopen, and installed-worker
lifecycle smoke tests also remain deployment gates.

Fresh independent review found no Critical issues and initially reported three
Important edge cases: retained pipes after normal Parser exit, cleanup-timeout
escape from the runtime containment filter, and comparison of independent
client counters in global status. Focused RED tests reproduced all three. The
cleanup grace now applies after every exit path, `ParserSupervisorException`
uses the existing bounded `InvalidOperationException` family, and global status
is projected only after atomic session-scoped sequence acceptance rather than
comparing unrelated counters. The covering suites passed `9/9` for Parser and
runtime containment and `5/5` for backend status contracts.
Final independent re-review found no Critical, Important, or Minor findings.
