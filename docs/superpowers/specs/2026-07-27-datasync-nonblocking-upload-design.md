# DataSync Nonblocking Upload Design

## Problem

The continuous DataSync worker performs handoff reconciliation, complete parser-job
draining, telemetry reconciliation, and upload polling on one loop. A large history
parse can occupy that loop for minutes. Recovery telemetry such as
`client_wechat_decrypt_export_result` then remains on disk, while committed parser
pages and heartbeats remain in the local outbox.

The observed Windows run identified 18 candidate databases, but the server received
neither the decrypt output counts nor any business data before the worker stopped.

## Design

Continuous mode will keep parser reconciliation single-threaded and move maintenance
work into one independent background loop. Each maintenance pass imports recovery
telemetry first, then runs the existing two upload slots. It runs immediately at
startup and then at the existing 15-second upload cadence.

The main reconciliation loop will only reconcile database handoffs and drain parser
jobs. Heartbeats remain on their existing independent cadence. Once a parser page
commits outbox rows, the maintenance loop can upload them without waiting for the
remaining history pages.

One maintenance loop owns telemetry imports, so ready files are never imported by
two worker paths concurrently. Upload concurrency remains exactly two. One-shot mode
keeps its deterministic sequential behavior.

## Failure And Shutdown

Telemetry reconciliation failures do not skip uploads. Any other non-cancellation
maintenance failure is contained and retried on the next fixed cadence, so neither
the maintenance loop nor the parser loop stops permanently. Cancellation stops hint
sources, heartbeat, and maintenance tasks; durable parser and outbox leases retain
the existing restart recovery behavior.

## Verification

- A blocking parser test must observe telemetry reconciliation and two upload slots
  before the parser is released.
- Telemetry and upload failures must recover on a later maintenance cadence without
  turning normal cancellation into a worker failure.
- Existing hint debouncing, single parser concurrency, heartbeat cadence, and
  one-shot ordering tests must continue to pass.
- Run the full Windows background tests, parser tests, release build, and release
  validation before publishing.
