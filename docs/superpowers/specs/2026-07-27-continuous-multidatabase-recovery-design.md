# Continuous Multi-Database Recovery Design

## Problem

Database discovery correctly finds the encrypted database set, but live capture
currently stops five seconds after the first matched key becomes idle. A real
manifest proved that an 18-database account published only
`db_storage/hardlink/hardlink.db`, with `requiredDatabasesComplete=false`.
Because this is not a message database, parsing and upload correctly produced no
business data.

## Requirements

- A five-second idle period may close one result batch, but must not complete the
  overall recovery cycle while unmatched databases remain.
- Every successfully decrypted batch is published immediately. Message batches can
  therefore enter parsing and upload while recovery continues.
- A database already completed in the current epoch is excluded from later capture
  batches so the same active auxiliary key cannot starve message databases.
- Capture continues automatically until all current candidates complete, the target
  process becomes unavailable, the existing 180-second no-match timeout expires, or
  the worker is cancelled.
- Keys, message content, and absolute user paths remain outside telemetry.
- Android and server deployment remain out of scope.

## Considered Approaches

1. Remove the five-second idle stop and publish only after the 180-second capture.
   This improves completeness but restores the long period with no visible data.
2. Export and publish directly inside the debugger candidate consumer. This gives
   true item-level streaming but risks blocking the bounded key-candidate channel
   during database export.
3. Treat the five-second stop as a batch boundary, publish the completed batch, then
   immediately capture again with completed paths excluded. This preserves bounded
   candidate handling, gives prompt useful output, and is the selected approach.

## Data Flow

`RecoveryCoordinator` owns a set of recovered relative database paths for the active
run. It passes a snapshot of that set to `IRecoveryCaptureAdapter`. The RC9 adapter
filters those paths from the next discovered candidate set while retaining the
original candidate count for diagnostics.

After a capture batch returns readable outputs, the coordinator publishes the batch
through the existing atomic handoff path. If the observation still reports unmatched
or failed candidates, the coordinator immediately starts another capture batch
instead of returning. DataSync continues importing, parsing, and uploading published
handoffs on its independent loops.

## Failure Behavior

- A successful partial batch is never discarded because another database is still
  unmatched.
- A batch with no new output follows the existing restart/circuit policy; it cannot
  spin every five seconds because the idle stop requires at least one new match.
- Failed exports remain eligible for a later batch because only successful recovered
  paths enter the exclusion set.
- Existing handoff idempotency and exported-item identities suppress duplicate files
  and duplicate business uploads.

## Verification

- A coordinator regression test must return one auxiliary partial batch followed by
  a message batch, prove both are published, and prove the second capture excludes
  the first database.
- An RC9 adapter regression test must prove completed relative paths are omitted from
  the capture delegate while candidate diagnostics retain the full discovery count.
- Existing five-second behavior must be described and tested as a batch boundary,
  not recovery completion.
- Full recovery, DataSync, parser, and WPF Release verification must pass before
  publishing version 1.0.24.
