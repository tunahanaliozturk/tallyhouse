# Query latency

```bash
dotnet run -c Release --project load/Tallyhouse.Load -- seed
dotnet run -c Release --project load/Tallyhouse.Load -- queries
dotnet run -c Release --project load/Tallyhouse.Load -- queries --settle
```

Every query here is one the dashboard sends, through the collector's HTTP API, timed by the client: 20 runs
after 3 warm-up runs, one query at a time.

The whole stack ran on one machine: Intel Core Ultra 7 255H, 16 cores, 32 GB, Windows 11, Docker Desktop
with 16 vCPUs and 15.3 GiB shared by every container, a single ClickHouse 26.8 node with default settings.

## The dataset

`seed` writes 100,000,824 events for 2 million users over 30 days straight into the fact table, with the
same columns and keys the loader writes, and the materialized view builds the retention rollup from them as
it would from loaded batches. [`load/seed.sql`](../../load/seed.sql) derives every row from a hash of its
row number, so the dataset is the same on every run.

| Event | Rows | Shape |
|---|---:|---|
| `page_view` | 77,248,000 | users and times uniform across the month |
| `feature_used` | 19,312,000 | the same way |
| `signup` | 2,000,000 | every user once, spread across the month |
| `activate` | 900,268 | 45% of users, within 3 days of signing up |
| `purchase` | 540,556 | 30% of activated users, one to three times, within 10 days |

Uniform page views are the hard case for retention and for a page-view-headed funnel: nearly every user is
active on nearly every day, so nothing collapses.

## Results

**Fresh** is straight after loading, with the table in 316 active parts, which is what a query sees during
steady ingest. **Settled** is after `--settle` has run `OPTIMIZE ... FINAL` on the fact table and the
rollup, leaving 44 parts, which is what yesterday's data looks like once background merges catch up.

| Query | Fresh p50 | Fresh p95 | Settled p50 | Settled p95 |
|---|---:|---:|---:|---:|
| 3-step funnel: signup, activate, purchase (7-day window) | 208 ms | 268 ms | 248 ms | 385 ms |
| 4-step funnel starting at page_view (7-day window) | 7,195 ms | 8,805 ms | 4,766 ms | 6,065 ms |
| 30-day retention, signup to page_view (rollup) | 1,029 ms | 1,227 ms | 558 ms | 770 ms |
| 30-day retention, signup to page_view (raw events) | 4,732 ms | 5,329 ms | 3,431 ms | 4,663 ms |
| Daily page_view counts where path = /pricing | 5,059 ms | 6,372 ms | 2,179 ms | 3,386 ms |
| Daily purchases in EUR or TRY | 75 ms | 127 ms | 37 ms | 46 ms |

The first version, measured with the same harness on the same dataset:

| Query | p50 | p95 |
|---|---:|---:|
| 3-step funnel | 179 ms | 277 ms |
| 4-step funnel starting at page_view | 10,007 ms | 13,709 ms |
| 30-day retention (rollup) | 3,921 ms | 5,443 ms |
| 30-day retention (raw events) | 5,387 ms | 6,916 ms |
| Daily page_view counts where path = /pricing | 3,569 ms | 5,201 ms |
| Daily purchases in EUR or TRY | 43 ms | 62 ms |

The 3-step funnel's answer, identical in every run and every version:

| Step | Users | From previous | Median time from start |
|---|---:|---:|---:|
| signup | 2,000,000 | 100.0 % | |
| activate | 900,268 | 45.0 % | 1d 11h 57m |
| purchase | 201,711 | 22.4 % | 4d 20h 08m |

## Reading the numbers

**How much to trust a single run.** An earlier settled run of the final version, on the same dataset,
measured 158 ms, 4,539 ms, 413 ms, 2,808 ms, 1,897 ms and 68 ms for the six queries. Runs on this laptop
differ by up to half, depending on what Docker Desktop's VM and the CPU's power management are doing, so
read these as orders of magnitude. During the final run the fact table also held about 130 million events
from other projects left by earlier runs; every query names one project, and the table's key starts with it.

**A funnel costs what its first step costs.** The 3-step funnel reads 3.4 million events and answers in a
quarter of a second. The 4-step funnel starts at `page_view`, reads 80 million, and takes seconds. The
second is the slowest thing the dashboard can ask for, and the README says so.

**Counts pay for `FINAL`, and less once merged.** The segment query counts events, so it reads the fact
table with `FINAL` and deduplicates at query time
([ADR 0002](../adr/0002-exactly-once-is-idempotent-storage-and-duplicate-blind-queries.md)). With 316 parts
that costs 5 seconds for 77 million page views; merged, 2.2. The purchases query reads the same way but
only half a million rows, so it barely notices.

**Retention reads the rollup, not the events.** The rollup is 10 million rows for this project against 100
million events, and needs no join.

## Two measurements that changed the design

### The rollup the spec suggested bought a quarter

The first retention rollup had a row per user, event and day. On this dataset it was 63 million rows after
merging, because an active user touches most days, and the 30-day matrix took 3.9 seconds through it
against 5.4 from raw events. Profiling showed reading was cheap and the cost was joining cohorts to per-user
arrays of return days, then the arithmetic on 256-bit values.

| Version, same data, measured in ClickHouse | Time |
|---|---:|
| day rollup, cohort join to arrays of return days | 2.2 to 3.0 s |
| day rollup, per-user 256-bit masks | 2.2 s |
| month rollup, 256-bit masks throughout | 1.5 s |
| month rollup, cohort-relative 64-bit mask per user | 0.55 to 0.8 s |

The rollup became a row per user, event and month with the days as a 32-bit mask, and each user's return
days are shifted so bit k means "came back k days after their first day". Through the API the matrix went
from 3.9 seconds to 1.0 fresh and 0.56 settled. [ADR 0006](../adr/0006-retention-reads-monthly-day-masks.md)
has the query.

### The funnel's cost was the grouping, not the algorithm

The first version of the page-view-headed funnel took 8.4 to 9.6 seconds across runs, and 10 seconds at the
median in the table above. Profiling its parts on the 80.7 million step events:

| Part of the query, 80.7M rows, 2M users | Time |
|---|---:|
| reading the rows | 0.56 s |
| `GROUP BY user_key` with `count()` only | 1.85 s |
| `groupArray` of (timestamp, step) tuples, sorted | 11.3 s |
| `groupArray` of one packed UInt64, sorted | 4.8 s |
| ClickHouse's `windowFunnel`, for comparison | 8.5 s |

The fold over each user's events was never the cost; carrying the events into it was. Each event became
one `UInt64`, `ts_ms * 8 + step`, first-step events that cannot start a chain in range were filtered before
grouping, and a run of consecutive first-step events is cut to its last before the fold. Same answer to the
last digit, 4.8 seconds settled, faster than `windowFunnel`, which measures no time to convert at all.
[ADR 0005](../adr/0005-funnels-follow-windowfunnel-and-measure-time.md) has the algorithm and what else was
tried.
