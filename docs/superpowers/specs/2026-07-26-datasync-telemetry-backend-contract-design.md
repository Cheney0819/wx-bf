# DataSync Telemetry And Backend Contract Design

Date: 2026-07-26
Status: approved direction, pre-implementation

## Goal

Preserve the useful operational visibility of the existing desktop-pet monitor while moving all durable delivery into the new DataSync boundary. Every business payload, status heartbeat, and operational event must carry a stable client identity and a request identity that the backend actually honors.

This design targets a single-user deployment. Public-read access, multi-tenant isolation, and general privacy hardening are outside this change. Data continuity, retry correctness, bounded storage, diagnostic usefulness, and failure recovery remain required.

## Existing Gap

The old monitor sends `session_id`, `source`, and `request_id` with messages, contacts, favorites, status, and events. The current DataSync foundation sends only `request_id`, the endpoint-specific array, and a token added immediately before transmission.

The backend currently ignores `request_id`. Messages have business-key deduplication and contacts/favorites use upserts, but event retries create duplicate rows. Status updates are naturally overwrite-oriented. New Worker event names are not recognized by `normalize_monitor_event`, so they do not appear in the dashboard timeline even if they reach `event_logs`.

## Architectural Boundaries

- Recovery remains the only owner of target processes, restart decisions, database keys, decryption, and readable database generations.
- Recovery never receives a server URL or token and never performs HTTP.
- DataSync remains the only owner of client identity, server settings, Parser execution, encrypted Outbox rows, status heartbeats, and HTTP delivery.
- Parser remains a pure readable-database parser with no process, key, identity, telemetry, token, or network ownership.
- Backend GET access and existing single-user authentication behavior remain unchanged in this change.
- WPF startup and installer cutover remain separate until the new contract passes local and Windows acceptance.

## Stable Client Identity

DataSync owns `DataSync/client-identity.json`, written atomically with schema 1:

```json
{
  "schemaVersion": 1,
  "sessionId": "client-cs-<guid>",
  "source": "client_cs",
  "createdAtUtc": "2026-07-26T00:00:00Z"
}
```

On first start, DataSync is given the legacy identity path `<application-root>/wechat_data/client_identity.json`. If it contains a valid `session_id`, DataSync imports that value and records source `client_cs`, preserving the existing dashboard session. The legacy file is read-only and is not deleted or rewritten.

If no valid legacy identity exists, DataSync creates `client-datasync-<guid>` with source `client_datasync`. Session IDs are 1-120 printable non-whitespace characters after whitespace normalization; sources are lowercase, 1-32 characters, and limited to ASCII letters, digits, underscore, and hyphen. Identity is not secret, so it is not DPAPI-protected.

Every Outbox plaintext is frozen with `request_id`, `session_id`, and `source` before encryption. The uploader adds only `token`; it never invents or changes identity.

## Backend Request Receipts

The backend adds `request_receipts`:

```text
endpoint, session_id, client_source, request_key, request_id,
response_json, created_at, completed_at
```

`request_key` is lowercase SHA-256 of the exact request ID. A unique key over `(endpoint, session_id, client_source, request_key)` identifies one logical request. `request_id` accepts 1-191 printable ASCII characters. Requests without it retain legacy behavior.

For each authenticated POST:

1. Resolve `session_id` and `source` exactly as today.
2. Insert the receipt inside the same MySQL transaction as the domain write.
3. If the unique row already exists, lock it and return its cached JSON response without repeating domain writes, event insertion, session mutation, or derived monitor-log insertion.
4. For a new receipt, perform the endpoint operation, save the exact success response in the receipt, and commit once.
5. Any exception rolls back the receipt and the endpoint operation together.

Receipts older than 30 days are pruned during database initialization. This bounds the one-minute heartbeat history while covering realistic retry and crash-recovery windows. Business-key deduplication remains the second line of defense for messages, contacts, and favorites.

## Operational Event Handoff

Recovery publishes low-frequency, sanitized schema-1 event envelopes under `Handoff/Telemetry/ready`. Each file is written and flushed as a temporary file, then atomically renamed to `<eventId>.json`.

```json
{
  "schemaVersion": 1,
  "eventId": "<64 lowercase hex>",
  "component": "recovery",
  "eventName": "recovery_capture_succeeded",
  "severity": "info",
  "code": "key_validated",
  "occurredAtUtc": "2026-07-26T00:00:00Z",
  "metrics": { "databaseCount": 18 }
}
```

Allowed payload data is limited to bounded booleans, integers, stable codes, counts, durations, and version identifiers. Keys, tokens, absolute paths, raw exception messages, database contents, and process memory never enter telemetry envelopes.

Recovery emits only state-changing events:

- `recovery_capture_started`
- `recovery_capture_succeeded`
- `recovery_capture_failed`
- `recovery_restart_started`
- `recovery_restart_completed`
- `recovery_restart_failed`
- `recovery_handoff_published`
- `recovery_circuit_opened`
- `recovery_coordinator_failed`

Telemetry publication is best effort relative to Recovery's primary work. A telemetry I/O failure is recorded locally with a stable code and does not change restart accounting, key state, generation state, or handoff publication.

## DataSync Telemetry Import And Outbox

DataSync watches and reconciles `Handoff/Telemetry/ready` in addition to database-ready manifests. It validates a 64 KiB maximum envelope, exact schema, filename/event identity, allowed component/name/severity/code shapes, bounded metrics, and rejects unknown fields.

One immediate SQLite transaction:

1. inserts the event identity into `imported_telemetry` with a unique `event_id`;
2. records a bounded local runtime event;
3. creates an encrypted `events` Outbox row containing stable client identity and request ID;
4. commits all three or none.

After commit, the source file is deleted. A crash before deletion causes an idempotent re-import. Invalid files move atomically to `Handoff/Telemetry/rejected`; their filenames are recorded locally without copying untrusted payload text.

DataSync's own state-changing events use the same event Outbox writer directly:

- `datasync_handoff_imported`
- `datasync_handoff_rejected`
- `datasync_parser_completed`
- `datasync_parser_failed`
- `datasync_upload_acknowledged`
- `datasync_upload_retry_scheduled`
- `datasync_upload_quarantined`
- `datasync_worker_started`

Upload-result events are emitted only for `messages`, `contacts`, and `favorites`. Acknowledging, retrying, or quarantining an `events` or `status` Outbox row never creates another telemetry row, preventing recursive event generation.

## Status Heartbeat

The normal-privilege DataSync Worker creates a durable status heartbeat immediately after startup and every 60 seconds. The payload always includes `session_id`, `source`, and a unique request ID. It includes `decrypt_ok` or `wechat_logged_in` only when a Recovery event has established a current value; omitted fields retain the backend's last known value.

Before inserting a new heartbeat, DataSync removes older unleased pending `status` rows for the same client. A currently leased status row is never mutated; one newer pending successor may coexist. This bounds offline growth without making an in-flight payload mutable.

Status publishing continues when no server settings exist. The newest encrypted heartbeat remains pending and is delivered after settings become available.

## Backend Event Mapping

`normalize_monitor_event` accepts both legacy and new names. New events map into the existing dashboard model:

| Event family | Dashboard stage |
| --- | --- |
| Recovery capture | `chatlog_key` |
| Recovery restart | `wechat_restart` |
| Recovery handoff | `decrypt_export` |
| Recovery circuit/coordinator failure | `recovery` error |
| DataSync handoff | `handoff` |
| Parser completion/failure | `parser` |
| Message upload | `messages_push` |
| Contact upload | `contacts_push` |
| Favorite upload | `favorites_push` |

The existing page and GET response shapes remain unchanged. `LOG_SOURCE_LABELS` adds `client_datasync` as `Windows 后台同步`.

## Backend Storage Hygiene

`event_logs` receives a 30-day retention deletion during initialization, matching request receipts. Existing `monitor_logs` count limits remain unchanged. Migration statements must ignore only duplicate-column or duplicate-index errors; other MySQL errors are raised so a broken schema does not start silently.

## Failure Semantics

- Missing server settings leave encrypted Outbox work pending.
- Network errors, 408, 429, redirects, and 5xx keep existing retry behavior.
- Other 4xx responses remain quarantined.
- A cached receipt response is byte-for-byte equivalent JSON data to the first successful response.
- A backend receipt failure rolls back the associated business or event write.
- A DataSync crash after backend commit but before local acknowledgement reuses the same request ID and receives the cached response.
- Telemetry failure never marks a Parser job, Recovery generation, or business upload as failed.

## Test Strategy

### .NET

- import a legacy identity and preserve `client_cs`;
- generate and reopen a new identity atomically;
- reject corrupt, oversized, or unsafe identity documents;
- assert every business Outbox plaintext contains identity before encryption;
- validate Recovery event envelopes and path containment;
- prove telemetry import, local event insertion, and encrypted Outbox insertion are atomic and idempotent;
- prove invalid telemetry is rejected without blocking later files;
- prove pending status coalescing and leased-status preservation;
- prove business upload outcomes create one telemetry event while event/status outcomes create none;
- prove Worker heartbeat continues across missing settings and restarts.

### Python backend

- validate request ID normalization and hashing;
- prove first receipt executes and caches, while a retry returns the cached response;
- prove a receipt and domain mutation roll back together on failure;
- prove event retry does not duplicate `event_logs` or `monitor_logs`;
- prove legacy requests without `request_id` retain current behavior;
- prove new event names produce the expected stage keys and titles;
- prove 30-day receipt/event retention SQL is installed;
- preserve the current messages/contacts/favorites/status/events success response shapes.

### End To End

Extend the loopback E2E so all three business requests carry the imported legacy identity. Add event and status requests, inject a response-loss retry, reopen DataSync, and assert the backend contract observes one logical event and one current heartbeat.

## Acceptance Gate

Before WPF or installer cutover:

- all .NET and Python suites pass;
- Release build has zero warnings and errors;
- the backend contract tests pass without a production database;
- a MySQL-backed smoke test proves receipt locking and cached responses;
- the Windows package E2E proves identity migration, Recovery telemetry handoff, event/status upload, DPAPI settings reopen, and Worker continuation;
- old and new real capture chains are still never started together.
