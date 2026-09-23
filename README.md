# Tallyhouse

A product analytics collector: events come in over HTTP at tens of thousands a second, land in ClickHouse
through Kafka, and come back out as funnels, retention, trends and sessions, counted exactly once however
many times a client or the pipeline delivered them.

Measured on one laptop (Intel Core Ultra 7 255H, Docker Desktop with 16 vCPUs), with every number
reproducible from `load/Tallyhouse.Load`:

- **20,000 events a second for ten minutes, 12,000,000 acknowledged and 12,000,000 stored.** Acknowledgement
  latency 8.1 ms at the median and 19.1 ms at p95; p99 was 18.8 ms over one minute and 90 ms over ten.
- **A three-step funnel over 100 million events in about 250 ms at the median**, with the median time to
  each step, and a 30-day retention matrix in 560 ms once merges have caught up, 1 second before.
- **A million events with a fifth of them delivered twice**, 98,999 of those racing past the deduplication
  layer into Kafka and the loader restarted halfway: every count equals the ground truth.
- **Sampling 10% of users** estimates page views within 0.73% and users within 0.86% of the full count.

```bash
docker compose up -d --build --wait

curl -s localhost:5180/v1/events/batch \
  -H 'authorization: Bearer thw_demo_write_key_do_not_use_outside_localhost' \
  -H 'content-type: application/json' \
  -d '{"events": [{"messageId": "m-1", "event": "signup", "userId": "ada",
                   "timestamp": "'"$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"'", "properties": {"plan": "pro"}}]}'
```

```json
{"accepted":1,"duplicates":0,"sampledOut":0,"quarantined":0,"notAccepted":[]}
```

Send it again and it is acknowledged as a duplicate and stored once. The dashboard is at
http://localhost:5181, and [`Tallyhouse.http`](Tallyhouse.http) walks through every endpoint.

![The funnel page over the 100-million-event benchmark dataset](docs/images/dashboard-funnel.png)

## Why this exists

"Log an event, count it later" is easy until three things meet. Tens of thousands of inserts a second
collapse a normalised table under index maintenance. A funnel, "did users who did A then do B then C, in
order, within a week", over a hundred million rows is minutes on a row store. And every hop between a phone
and a database delivers at least once, so a conversion retried by an SDK, or replayed by a loader after a
crash, is counted twice unless something stops it. A collector that is fast and double-counts is worse than
one that is slow, because nobody notices.

## What is worth looking at

| | |
|---|---|
| The ingest order, and why ids are remembered after Kafka has the event | [`IngestPipeline.cs`](src/Tallyhouse.Application/Ingestion/IngestPipeline.cs) |
| One malformed event cannot fail its batch | [`EventNormalizer.cs`](src/Tallyhouse.Application/Ingestion/EventNormalizer.cs) |
| The fact table, deduplicated down to the message id | [`001_schema.sql`](src/Tallyhouse.Infrastructure/ClickHouse/Schema/001_schema.sql) |
| `windowFunnel`'s algorithm with time to convert, packed into one UInt64 per event | [`FunnelSql.cs`](src/Tallyhouse.Infrastructure/ClickHouse/Queries/FunnelSql.cs) |
| Retention from per-user monthly day masks, with no join | [`AnalyticsQueries.cs`](src/Tallyhouse.Infrastructure/ClickHouse/Queries/AnalyticsQueries.cs) |
| Sessions recomputed per day and swapped in atomically | [`Sessionizer.cs`](src/Tallyhouse.Infrastructure/ClickHouse/Sessionizer.cs) |
| Pausing ClickHouse and Kafka for real | [`ChaosTests.cs`](tests/Tallyhouse.IntegrationTests/ChaosTests.cs) |
| Funnels checked against `windowFunnel` and a reference model | [`QueryTests.cs`](tests/Tallyhouse.IntegrationTests/QueryTests.cs) |
| **Two measurements that changed the design** | [`query-latency.md`](docs/benchmark-results/query-latency.md) |

## How it works

```text
 SDK ── POST /v1/events/batch ──▶ Collector ── validate, redact, sample,
                                     │         look up recent ids in Redis
                                     │
                          produce, acks=all ──▶ Kafka ──▶ Loader ──▶ ClickHouse
                                     │          (7 days)   │  batch insert,     events (ReplacingMergeTree)
                        202 ◀────────┘                     │  then commit       user_event_months (bit masks)
                                                           │                    sessions, quarantine
                                                   Sessionizer ◀── dirty days
 Dashboard ─▶ nginx (adds read key) ─▶ Collector ── funnel, retention, segment, sessions ──▶ ClickHouse
```

**Acknowledged means durable, not queryable** ([ADR 0001](docs/adr/0001-acknowledge-when-durable-not-when-queryable.md)).
The collector answers 202 once Kafka has every event in the request from all in-sync replicas. ClickHouse
can be down, merging or restarting and ingest does not notice; the loader's lag grows and drains afterwards.
`ChaosTests` pauses the ClickHouse container, sends a thousand events that are all acknowledged, and checks
all thousand arrive. Kafka being down is the one thing that stops acknowledgements, with a 503 and
`Retry-After`.

**Exactly once is at-least-once delivery into storage that cannot count a duplicate**
([ADR 0002](docs/adr/0002-exactly-once-is-idempotent-storage-and-duplicate-blind-queries.md)). Redis remembers
message ids for ten minutes, which catches client retries cheaply, and it fails open because the guarantee is
elsewhere: the fact table's key ends in the message id, and every query is written against a measure a
duplicate cannot move. A user either reached a funnel step or did not; a day bit ORed twice is the same bit.
Only event counts pay for `FINAL`.

**Late events are counted, not refiled** ([ADR 0003](docs/adr/0003-late-events-go-to-a-labelled-bucket.md)).
An event more than two hours old when it arrives is stored and flagged late. Daily numbers stop moving two
hours after the day ends, and late events are reported beside them instead of silently changing a day
somebody already put in a report.

**Sessions are recomputed, not maintained** ([ADR 0004](docs/adr/0004-sessions-are-recomputed-not-maintained.md)).
A late event that bridges two sessions merges them on the next pass, because a day's sessions are rebuilt
from its events and swapped in with `REPLACE PARTITION`, with no incremental state to correct.

**Funnels read events; retention reads a rollup**
([ADR 0005](docs/adr/0005-funnels-follow-windowfunnel-and-measure-time.md),
[ADR 0006](docs/adr/0006-retention-reads-monthly-day-masks.md)). Whether a user converted depends on the order
and spacing of their events, so no rollup can answer a funnel exactly. Retention only needs days, and a
user's days in a month fit in 32 bits.

**Sampling keeps whole users** ([ADR 0007](docs/adr/0007-sample-users-not-events.md)), **schemas are strict and
only grow in place** ([ADR 0008](docs/adr/0008-strict-schemas-that-only-grow.md)), and **the dashboard holds no
credential** ([ADR 0009](docs/adr/0009-the-dashboard-holds-no-credential.md)).

## Layers

| Project | Holds | May reference |
|---|---|---|
| `Tallyhouse.Domain` | schemas and how they may evolve, lateness, sampling, identity, projects | the base library only |
| `Tallyhouse.Application` | the ingest pipeline, normalisation, quarantine replay, query definitions and their bounds, the ports it needs | Domain |
| `Tallyhouse.Infrastructure` | Kafka log and loader, Redis deduplication, the EF Core catalog, ClickHouse schema, writer, queries and sessionizer | Application |
| `Tallyhouse.Api` | the collector: ingest, catalog and query endpoints | Infrastructure |
| `Tallyhouse.Loader` | the loader and sessionizer host | Infrastructure |

`ArchitectureTests` checks this against what each compiled assembly actually references, including that
neither inner layer names Kafka, ClickHouse, Postgres, Redis, ASP.NET or any `Microsoft.Extensions` package.

## Two measurements that changed the design

**The rollup the spec suggested bought a quarter.** One row per user, event and day came to 63 million rows
for 100 million events, because active users touch most days, and 30-day retention through it took 3.9
seconds. A row per user, event and month with the days as a bit mask is 10 million rows, the query needs
no join, and the same matrix takes 0.56 seconds.

**The funnel's cost was the grouping, not the algorithm.** Profiling a four-step funnel over 80 million step
events showed that reading them took 0.56 s and collecting them per user as (timestamp, step) tuples took 11.
Packing each event into one UInt64 and cutting runs of first-step events before the fold took it from 10
seconds to under 5, faster than ClickHouse's own `windowFunnel`, which measures no time at all.

Both are in [`docs/benchmark-results/query-latency.md`](docs/benchmark-results/query-latency.md), next to
the first version's numbers.

## Running it

| | |
|---|---|
| Everything | `docker compose up -d --build --wait` |
| Collector API | http://localhost:5180 (OpenAPI at `/openapi/v1.json`) |
| Dashboard | http://localhost:5181 |
| ClickHouse | http://localhost:8123, user and password `tallyhouse` |
| Demo keys | in [`compose.yaml`](compose.yaml); public, localhost only |

To reproduce the measurements against that stack:

```bash
dotnet run -c Release --project load/Tallyhouse.Load -- seed          # 100 million events, about five minutes
dotnet run -c Release --project load/Tallyhouse.Load -- queries --settle
dotnet run -c Release --project load/Tallyhouse.Load -- exactly-once --restart-loader
dotnet run -c Release --project load/Tallyhouse.Load -- sampling
dotnet run -c Release --project load/Tallyhouse.Load -- soak          # see ingest-soak.md for where to run it
dotnet run -c Release --project load/Tallyhouse.Load -- traffic       # a live stream for the dashboard
```

## Tests

| Suite | What it covers |
|---|---|
| `tests/Tallyhouse.UnitTests` (102) | validation, schema evolution, lateness, sampling, identity, the ingest order, query limits, the layering |
| `tests/Tallyhouse.IntegrationTests` (16) | Postgres, Redis, Kafka and ClickHouse in containers: ingest to query, quarantine and replay, funnels against `windowFunnel`, retention rollup against raw events, forced duplicates with merges stopped, sessions merged by a late event, ClickHouse and Kafka paused |
| `web` (15 Vitest, 3 Playwright) | response contracts, charts, the journeys against the compose stack, axe on every screen |

CI runs all of it, plus the licence audits for NuGet and npm, formatting, a benchmark smoke run, a check that
the committed OpenAPI document and generated TypeScript types match the server, and the compose quick start
with real assertions.

## Stack

.NET 10 and C# 14, ASP.NET Core minimal APIs with source-generated JSON, Confluent.Kafka on Apache Kafka 4.3,
ClickHouse 26.8 through the official ClickHouse.Driver, EF Core 10 on Postgres 18 for the catalog,
StackExchange.Redis, OpenTelemetry. Vue 3, TanStack Query and Zod for the dashboard. xUnit v3, Testcontainers,
Playwright and BenchmarkDotNet for the proofs. Every dependency is permissively licensed, checked on every
build.

## Limitations

The honest list is in [`docs/operations.md`](docs/operations.md#known-limitations). The ones worth knowing
before anything else: a funnel headed by the highest-volume event takes seconds, not milliseconds; sessions
end at midnight UTC; there is no identity stitching from anonymous to known users; and the dashboard is a
read window onto one project, meant to sit behind your own authentication.

## Licence

MIT.
