# Operating Tallyhouse

Two processes and four stores. The collector (`Tallyhouse.Api`) takes events and queries; the loader
(`Tallyhouse.Loader`) moves events from Kafka into ClickHouse and recomputes sessions. They share nothing at
runtime except Kafka and ClickHouse, and they scale and fail independently.

| Store | Holds | If it is down |
|---|---|---|
| Kafka | every accepted and quarantined event, for 7 days | the collector answers 503; nothing is acknowledged |
| ClickHouse | the fact table, the retention rollup, sessions, quarantine | queries fail; ingest carries on, the loader's lag grows |
| Postgres | projects, keys, schemas | catalog changes fail; ingest carries on from the in-memory snapshot |
| Redis | recent message ids, 10 minutes | duplicates reach Kafka and are removed by ClickHouse; nothing fails |

## Configuration

Connection strings are in `ConnectionStrings`; everything else is under `Tallyhouse`. Environment variables
use `__` for `:` (`Tallyhouse__Ingestion__Watermark`).

| Setting | Default | What it trades |
|---|---|---|
| `ConnectionStrings:Kafka` | required | bootstrap servers |
| `ConnectionStrings:ClickHouse` | required | `Host=...;Port=8123;Username=...;Password=...;Database=...` |
| `ConnectionStrings:Postgres` | required (collector) | the catalog |
| `ConnectionStrings:Redis` | optional | absent disables the fast deduplication layer, which only costs duplicate traffic to Kafka |
| `Tallyhouse:OperatorToken` | none | creates projects, registers schemas, replays quarantine; absent disables those endpoints |
| `Tallyhouse:Ingestion:Watermark` | `02:00:00` | how long a day stays open to late events (ADR 0003) |
| `Tallyhouse:Ingestion:MaxClockSkew` | `00:05:00` | how far in the future a client clock may be before its events are quarantined |
| `Tallyhouse:Ingestion:Retention` | `90.00:00:00` | older events are quarantined; must match the ClickHouse TTL |
| `Tallyhouse:Ingestion:MaxBatchSize` | `500` | events per batch request |
| `Tallyhouse:Ingestion:DeduplicationWindow` | `00:10:00` | Redis memory against how late a retry is still caught before Kafka |
| `Tallyhouse:Kafka:Partitions` | `12` | loader parallelism ceiling; set before the topic exists |
| `Tallyhouse:Kafka:DeliveryTimeout` | `00:00:10` | how long a request waits for Kafka before answering 503 |
| `Tallyhouse:Kafka:LingerMilliseconds` | `5` | producer batching against acknowledgement latency |
| `Tallyhouse:Kafka:MaxQueuedMessages` | `200000` | the collector's back-pressure point; past it requests get 503 at once |
| `Tallyhouse:Loader:MaxBatchSize` | `50000` | rows per ClickHouse insert |
| `Tallyhouse:Loader:MaxBatchDelay` | `00:00:01` | freshness against insert size |
| `Tallyhouse:Sessions:InactivityGap` | `00:30:00` | the session definition |
| `Tallyhouse:Sessions:Interval` | `00:00:15` | how often dirty days are recomputed |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none | set it to export traces and metrics over OTLP |

`Tallyhouse:Demo:*` seeds a project with fixed keys. It exists for the compose stack; never set it anywhere
reachable from outside a laptop.

## What to watch

Every instrument is under the `Tallyhouse` meter.

| Metric | Healthy | Worth a look |
|---|---|---|
| `tallyhouse.loader.lag` | near zero, sawtooth with the batch delay | growing for more than a few minutes: ClickHouse is down or slower than ingest |
| `tallyhouse.loader.write.failures` | zero | any sustained rate: ClickHouse is refusing inserts; lag will follow |
| `tallyhouse.loader.unreadable` | zero, always | anything: a producer and loader disagree about the record format |
| `tallyhouse.ingest.events{outcome="quarantined"}` / all | below 1% | a jump: a client release is sending events its schema does not allow |
| `tallyhouse.ingest.events{outcome="duplicate"}` / all | low single digits | high: an SDK retrying too aggressively |
| `tallyhouse.dedup.bypassed` | zero | non-zero: Redis is unreachable; duplicates now go to Kafka |
| `tallyhouse.ingest.late_events` | small and steady | a jump: a client buffering for hours |
| `http.server.request.duration` for `/v1/events/batch` | p99 under 25 ms | rising: Kafka is slow; 503s follow once the producer queue fills |
| `tallyhouse.query.duration{kind}` | see docs/benchmark-results | far above it: merges have fallen behind, or a query is scanning more than it should |
| `tallyhouse.sessions.recomputed_days` | a few per pass | zero while events flow: the sessionizer is failing (check its logs) |

## Alerts

- Loader lag growing for 10 minutes. Kafka keeps 7 days, so this is not yet data loss, but it is a
  ClickHouse problem that will not fix itself.
- Any 503 from `/v1/events` for 2 minutes. Clients are being refused.
- Quarantine share above 5% for 15 minutes.
- `tallyhouse.loader.unreadable` above zero, ever.

## Runbooks

**ClickHouse is down.** Ingest keeps acknowledging; nothing needs doing on the collector. The loader retries
its current batch with backoff up to 30 seconds and consumes nothing new. Bring ClickHouse back and watch
the lag drain. If the outage outlasts `max.poll.interval.ms` (10 minutes) the loader leaves its consumer
group and rejoins from its last committed offset when it next polls; the rows it replays are removed by
deduplication. Queries fail for the duration.

**Kafka is down or slow.** The collector answers 503 with `Retry-After: 1`, and SDKs retry with the same
message ids. Nothing acknowledged is lost. The readiness probe reports not ready, so a load balancer stops
routing to collectors that cannot write. Fix Kafka.

**Redis is down.** Nothing to do urgently. `tallyhouse.dedup.bypassed` rises and duplicate retries reach
Kafka and ClickHouse, where they are merged away.

**Postgres is down.** Ingest and queries continue on the last catalog snapshot. Creating projects,
registering schemas and changing settings fail until it is back.

**A client release is being quarantined.** `GET /v1/quarantine` with the project's read key shows the
reason on each event. Either fix the client, or grow the schema if the new property is legitimate
(`PUT /v1/projects/{id}/schemas/{event}/versions/{n}`), then replay each event with
`POST /v1/projects/{id}/quarantine/{quarantineId}/replay`. Replay judges lateness against the original
arrival time. A breaking change needs a new version, which clients opt into by sending `version`.

**Sessions look stale.** Check the loader's logs for "Sessionizer pass failed". A pass that fails keeps its
cursor, so the next pass recomputes the same days. To force a full recompute of the last three days,
restart the loader.

**Queries are slow after a bulk import.** Freshly inserted partitions are several overlapping parts until
background merges finish, which makes `FINAL` expensive (docs/benchmark-results measures both states).
Wait, or run `OPTIMIZE TABLE events PARTITION <yyyymmdd> FINAL` for the affected days.

## Known limitations

- Sessions end at midnight UTC, and a project's sessions are partitioned per day, so partition count grows
  with projects times retained days (ADR 0004).
- A funnel headed by the highest-volume event scans all of it. Four steps starting at 77 million page views
  take seconds, not milliseconds (docs/benchmark-results).
- On one machine with everything co-located, ingest at 20,000 events a second keeps a p99 under 25 ms for a
  minute but not for ten, where it reached 90 ms with stalls of over a second
  (docs/benchmark-results/ingest-soak.md). The cause is not yet isolated.
- Retention follows at most 63 days, over a span of at most 256 days, because the per-user masks are 64 and
  256 bits wide.
- Identity is `userId` or `anonymousId`; there is no alias step joining an anonymous history to a user who
  later signs up.
- Quarantine keeps one row per delivery, so a client retrying a batch with an invalid event in it
  quarantines that event once per retry.
- The dashboard is a read window onto one project and has no login of its own (ADR 0009).
