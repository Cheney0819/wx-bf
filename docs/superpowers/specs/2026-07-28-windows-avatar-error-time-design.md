# Windows Avatar, Error Recovery, and Event Time Design

## Scope

Fix three issues verified on the Windows desktop-pet pipeline:

1. Parsed contacts and chat messages have empty avatars even when `contact.db` contains avatar URLs.
2. A recovered capture or parser error remains in `operational_state.error`, so later heartbeats repeatedly report it.
3. The monitor timeline shows the delayed upload time instead of the client event occurrence time.

Android behavior, the fixed server-token flow, recovery restart policy, parser pagination, and existing monitor features are out of scope.

## Avatar Data Flow

The Windows parser will inspect the `contact` table schema before selecting rows. When the schema contains `small_head_url` and/or `big_head_url`, it will select the available columns and choose the trimmed small URL first, then the trimmed big URL. If neither column exists, the parser will continue to emit an empty avatar so older Weixin databases remain readable.

The same contact projection will be used by the paginated contact export and the targeted contact lookup used for message name resolution. Once contact lookup completes, each received message will copy the avatar of its resolved sender. For direct chats this is the chat contact; for group chats this is the sender contact. Sent messages keep an empty avatar because the monitor already renders the local-user fallback.

The monitor will treat `https://` and `http://` avatar values as image URLs and all other non-empty values as legacy Base64 image data. Both contact-list and chat images will replace themselves with the existing initial-letter fallback if loading fails. No avatar proxy, download cache, or database migration is introduced.

## Error Lifecycle

`TelemetryOutboxWriter` remains the single place that derives operational state from ordered client telemetry. Recovery errors and warnings continue to write `operational_state.error`. Existing recovery success events continue to clear it.

The following verified recovery signals will also clear the old error:

- `client_wechat_decrypt_export_result` with code `partial_success`, because readable databases have already been handed to the parser.
- `datasync_parser_completed` with code `success`, because the database pipeline has completed successfully.

A later failure event can write a new error again. Heartbeats remain unchanged structurally and include `error` only when the current state value is non-empty and not one of the two legacy transient values already filtered by the writer.

## Event Occurrence Time

For `/api/events`, the monitor server will parse `payload.occurred_at_utc` only when it is an ISO-8601 string with an explicit UTC offset. It will convert valid values to a timezone-naive UTC `datetime`, matching the existing MySQL column handling, and pass that time to `log_event`. Missing, malformed, offset-free, or implausibly future values will fall back to server receipt time.

The monitor log's `first_event_at` and `last_event_at` remain the displayed and aggregation timestamps. The row's MySQL `created_at`/`updated_at` values continue to record server persistence activity for internal diagnosis. Since the test database will be cleared, no historical rewrite or migration is required.

## Failure Handling

- Missing avatar columns: emit empty avatars and continue parsing.
- Empty or whitespace avatar values: fall back from small URL to big URL, then to the initial-letter UI.
- Broken or blocked remote image: browser `error` handler replaces the image with the existing fallback.
- Invalid client occurrence time: use server receipt time without rejecting the event.
- New post-recovery error: overwrite the cleared state through the existing occurrence-ordered operational-state update.

## Verification

Automated coverage will prove:

- New and legacy contact schemas both parse successfully.
- Small URL is preferred, big URL is the fallback, and direct/group received messages receive the correct avatar.
- `partial_success` and parser completion clear a previous error, while a later failure restores a current error.
- A heartbeat after recovery omits `error`.
- Monitor avatar rendering supports URL and legacy Base64 values with an error fallback.
- Valid `occurred_at_utc` controls the event timestamp and invalid values fall back to receipt time.

After unit tests, build the Windows solution and installer, install the new build, and rerun the existing true-device path without restarting Weixin manually. Confirm contact/chat avatars appear, recovered errors stop recurring in heartbeats, delayed events show their original occurrence times, parser/upload events succeed, and Weixin restart count remains zero.
