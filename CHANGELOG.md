# Changelog

## 1.0.0

The first release: the collector, the loader, the queries and the dashboard, with the measurements in
`docs/benchmark-results` taken on this version.

### Ingest

- `POST /v1/events` and `/v1/events/batch` (up to 500) answer 202 once every event is acknowledged by Kafka
  (`acks=all`, idempotent producer), and 503 with `Retry-After` when the log cannot take them.
- Events are validated field by field from the raw JSON against the registered schema, so one malformed
  event is quarantined and the rest of its batch is unaffected.
- Invalid events are made durable on a quarantine topic in the same append, listed with their reason, and
  replayed by the operator once the schema or client is fixed.
- Configured properties are redacted before anything is stored, in the fact table and in quarantine.
- Recently seen message ids are kept in Redis for ten minutes and remembered only after Kafka has the event.
  The layer fails open.
- Events older than the two-hour watermark are stored and flagged late; events far in the future or past
  retention are quarantined.
- Deterministic per-user sampling per event type, with the threshold stored on each row.

### Storage and queries

- A `ReplacingMergeTree` fact table keyed down to the message id, keeping the earliest delivery of an event.
- Funnels up to eight steps with conversion and median time to each step, checked against `windowFunnel`.
- Retention from a per-user monthly day-mask rollup, with a raw-events path that returns the same answer.
- Daily segments with property filters and late events reported apart.
- Sessions recomputed per project and day and swapped in atomically.
- Sampled measures scaled per ADR 0007 and marked as estimates.

### Catalog

- Projects, write and read keys (hashed), settings and schemas in Postgres through EF Core.
- Schemas may only grow in place; breaking changes are refused with the list of what they would break.
- Every collector serves validation from an in-memory snapshot and polls one revision number.

### Dashboard

- Funnels, retention, trends, sessions and quarantine, in Vue 3, reading one project through nginx, which
  holds the read key.
- Response schemas checked against types generated from the API's OpenAPI document.

### Structure

- Domain, Application and Infrastructure projects under two hosts, the collector and the loader, with the
  dependency direction checked against the compiled assemblies by `ArchitectureTests`.

### Measurement

- `load/Tallyhouse.Load`: 100-million-event seed and query timing, ingest soak, the exactly-once and
  sampling proofs.
- `benchmarks/Tallyhouse.Benchmarks`: the collector's CPU cost per event.
