# Task 2 Recovery Report

Status: DONE_WITH_WINDOWS_ACCEPTANCE_PENDING

## Scope

Windows Recovery hardening only. Android, WPF cutover, installer, parser, DataSync upload, and server code were not changed by this task.

## Changes

- Database generations now include WAL prefix/metadata and SHM metadata fingerprints, so WAL-only activity invalidates stale completed generations.
- Persisted-key reuse returns resolved and unresolved required databases. Readable partial results publish immediately, while unresolved databases continue into live capture.
- Malformed validated-key records are quarantined one record at a time, including valid envelopes with malformed JSON metadata; later valid keys are still tried.
- Pending capture tickets are scoped by data-root identity, epoch, and current database salt fingerprints.
- Data-root discovery reads the supported xwechat INI redirect, rejects unbound multi-account ambiguity, and binds the selected account using the active process's database handles.
- Runtime PID, session, and executable path now flow from root binding into current capture. Restart preparation scans only replacement PIDs in the same session and executable path, and the process controller terminates only the bound executable group.
- Unsupported loaded modules are classified before capture backend creation and do not consume restart budget.
- Breakpoint restoration uses the worker token and a five-second deadline. A pre-cancelled cleanup still performs one immediate restore attempt; terminal failure maps to `breakpoint_restore_failed` for the controlled restart path.

## Verification

```text
Task 2 Core filter:       24 passed, 0 failed
Task 2 Recovery filter:   63 passed, 0 failed
Wx411.Core.Tests:        215 passed, 0 failed
Recovery.Tests:          120 passed, 0 failed
Background.Tests:          8 passed, 0 failed
DataSync.Tests:           156 passed, 0 failed
git diff --check:           clean
```

The full `windows-background/DesktopPet.Background.sln` test run passed all four test projects.

## Windows Acceptance

This macOS host compiled the Windows-targeted projects but did not exercise live Windows DPAPI, Restart Manager handle ownership, debugger attach, breakpoint byte restoration against a real process, Authenticode identity, or a real target restart. Those paths remain part of the Windows release acceptance pass.

All unrelated tracked and untracked shared-worktree changes were preserved and excluded from the Task 2 commit.
