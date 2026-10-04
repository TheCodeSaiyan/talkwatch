# Traces and metrics

TalkWatch sends traces and metrics over OpenTelemetry when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, such as `http://otel-collector:4317`, and sends nothing otherwise. The other standard `OTEL_` settings (protocol, headers, service name) work as usual.

## What you get

**Traces** of requests to TalkWatch, its requests to the console, its database work, and each poll as a span of its own, marked as an error when a poll fails or the console's responses change shape.

**Metrics** for requests, the HTTP client and the .NET runtime, and TalkWatch's own:

| Metric | Tags |
|---|---|
| `talkwatch.polls` | `result`: `ok`, `rate_limited`, `drift`, `failed` |
| `talkwatch.calls.stored` | `source`: `poll`, `live`; `change`: `added`, `updated` |
| `talkwatch.alert.deliveries` | `channel`; `result`: `sent`, `retrying`, `gave_up` |
| `talkwatch.audio.copies` | `kind`; `result`: `copied`, `unavailable`, `failed` |
| `talkwatch.retention.removed` | `what`: `calls`, `audio_files` |

`talkwatch.polls{result="failed"}` climbing is worth an alert of its own: it's how a stalled import shows. One did stall on a real console, for two hours and a quarter, before the fault behind it was fixed.

## What's kept out

- **Acknowledge-link tokens.** Request paths under `/a/` are recorded as `/a/{token}`.
- **Requests to alert channels** aren't traced at all. A Telegram bot token is part of its request address, and an ntfy topic is often its only secret.
- **Values in database statements.** Statements are recorded with their placeholders, never the values.

Logs go to standard output as usual, where a log collector can pick them up; they aren't sent over OpenTelemetry.

The health check, `/healthz`, isn't traced. It answers once the database is migrated and the app has started.
