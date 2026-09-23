# 2. Exactly-once counting is idempotent storage plus queries that cannot see duplicates

Status: accepted

## Context

Every hop in this pipeline delivers at least once. A client retries when its request times out after the
server already had it. Two requests carrying the same event can arrive together. The loader replays a
batch after a crash between inserting it and committing the offset. None of that can be engineered away;
the only question is where duplicates stop.

The obvious place is a set of recently seen message ids in front of everything. The spec suggested keeping
them for the watermark, two hours. At 20,000 events a second that is 144 million keys, several gigabytes
of Redis for one optimisation, and it still does nothing for the loader replaying a batch, which happens
after the collector.

## Decision

Two layers, with different jobs.

**Redis, sized for client retries.** Message ids are remembered for ten minutes, which covers the retry
behaviour of every SDK worth supporting. The collector checks before producing and remembers only after
Kafka has acknowledged. The reverse order is simpler and loses data: if the id were remembered first and
the append then failed, the client's retry would be answered "duplicate" for an event that was never
stored. The layer fails open. With Redis gone every event looks new and reaches the log, and nothing is
refused. It never uses a probabilistic structure, because a Bloom filter's false positive is a dropped
first delivery.

**The fact table is the guarantee.** `events` is a `ReplacingMergeTree` whose sorting key ends in
`message_id`, so two deliveries of one event share a key and collapse on merge. The version column is
`arrival_rank`, computed from `received_at` so the earliest delivery survives: it is the copy whose
lateness was judged first, and it makes a query that filters `NOT is_late` without `FINAL` agree with one
that uses it.

Until a merge runs, both copies exist. So every query is written against a measure duplicates cannot move:

| Query | Measure | Why duplicates do not matter |
|---|---|---|
| Funnel | per-user step chain | a second copy has the same timestamp and step, so it cannot start, extend or end a chain |
| Retention | OR of per-user day bits | setting a bit twice is setting it once |
| Unique users | `uniqExact(user_key)` | a set |
| Event counts | `count()` under `FINAL` | the one place duplicates would count, so it pays for `FINAL` |

Rollups follow the same rule. A materialized view sees inserts, not merges, so a counting rollup would
count every duplicate the fact table has not merged yet. The only rollup here holds per-user day masks,
combined with `groupBitOr`, which is idempotent.

## Consequences

`ExactlyOnce` in the load harness sends a million events with a fifth delivered twice, half of those in a
second request sent at the same moment as the first so they race past Redis, and restarts the loader
halfway through. The queries report the ground truth exactly. The number of rows physically present before
merging is printed next to it, so the run shows the duplicates were really there.

`QueryTests` stops merges, writes every event twice and checks that funnel and segment answers do not move.

`FINAL` costs something on a partition that has not been merged yet, which is the partitions receiving
events right now. On the benchmark dataset it added about two seconds to a segment query over a
freshly bulk-loaded month and nothing once the partitions had merged (docs/benchmark-results).

## Alternatives considered

**Deduplicate in the loader with a large in-memory set.** It moves the problem instead of solving it: a
loader restart empties the set, and two loaders would each need all of it.

**`count(DISTINCT message_id)` instead of `FINAL`.** Exact, and it builds a hash set of every message id in
range on every query, forever. `FINAL` costs nothing on a partition that has merged, which is every
partition older than a few minutes.

**Kafka transactions end to end.** ClickHouse is not a transactional Kafka sink, so the guarantee would
stop at the loader anyway.
