# 5. Funnels follow windowFunnel's algorithm and also measure time to convert

Status: accepted

## Context

A funnel answers two questions: how many users got through each step, and how long it took them. ClickHouse
has `windowFunnel`, a well-tested aggregate that answers the first. It does not answer the second, and the
median time from signup to purchase is half of what someone opens a funnel for.

A funnel cannot be pre-aggregated without losing exactness. Whether a user converted depends on the order of
their events and the gaps between them, and any rollup coarser than the events themselves either drops the
order or drops the gaps. So a funnel reads the fact table, and the work is in making that read cheap.

## Decision

The funnel is computed per user with `arrayFold` over that user's step events, carrying for each level the
start of the chain that reached it and the time it was reached. The algorithm is `windowFunnel`'s: a
first-step event starts a chain, replacing any earlier start because a later start leaves more of the
window; a step-i event extends the chain that reached step i-1 if it is within the window of that chain's
start; a user's level is the deepest step any chain reached. Simultaneous events are ordered by step, so the
result does not depend on how ClickHouse happened to read the rows.

Because the SQL is a re-implementation, it is checked against the original: `QueryTests` generates 400 users
with random step sequences, including first steps outside the range and chains that finish after it, and
requires the levels to equal `windowFunnel`'s and the medians to equal a plain C# implementation of the
algorithm.

The first version took about 9 seconds for a four-step funnel headed by page views over the benchmark
dataset (77 million page views, 80 million step events). Profiling it in ClickHouse showed that the fold was
not the cost:

| Part of the query, 80.7M rows, 2M users | Time |
|---|---:|
| reading the rows | 0.56 s |
| `GROUP BY user_key` with `count()` only | 1.85 s |
| `groupArray` of (timestamp, step) tuples, sorted | 11.3 s |
| `groupArray` of one packed UInt64, sorted | 4.8 s |
| `windowFunnel` itself | 8.5 s |

So the changes were about what the aggregation carries:

- Each event is one `UInt64`, `ts_ms * 8 + step`. Sorting it sorts by time and then step, and it is half the
  size of the tuple.
- First-step events outside the date range are filtered before grouping, since they can never start a chain.
- After sorting, a run of consecutive first-step events is cut to its last element, because each one only
  replaces the one before it. For a page-view-headed funnel that shrinks what the fold walks from dozens of
  elements per user to a handful.

With the same answer to the last digit, the four-step funnel went from 8.4 to 9.6 seconds across runs to 3.8
to 5.8, faster than `windowFunnel` while also measuring time. A funnel headed by a lower-volume event, the usual case, runs
in a few hundred milliseconds (docs/benchmark-results).

## Consequences

A funnel has at most eight steps, because the step index is three bits of the packed value. The limit is
checked on the request.

A funnel whose first step is the highest-volume event in the project scans all of that event in range and
builds a per-user array from it. On the benchmark hardware that is seconds, and it is the slowest query the
dashboard can ask. The README says so.

The reported time to a step is from the start of the chain that reached it, which is the most recent
qualifying first step, as in `windowFunnel`. It is not the time since the user's first-ever first step.

## Alternatives considered

**`windowFunnel` for levels and nothing for time.** Faster to write, slower to run on this data, and it
answers half the question.

**A projection of the fact table sorted by user.** It would let the per-user aggregation stream instead of
hash. On a copy of the data sorted that way the packed aggregation took 2.8 seconds instead of 4.8, for a
second copy of the table on disk. Not worth it for the one query shape that benefits.
