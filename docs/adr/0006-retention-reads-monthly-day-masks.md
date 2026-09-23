# 6. Retention reads per-user monthly day masks

Status: accepted

## Context

Retention groups users by the first day they did a start event, and counts for each later day how many of
them did a return event on it. Unlike a funnel it only needs days, not timestamps, so it can be answered
from a rollup. The spec suggested one: a row per user, event and day.

That rollup was built, and measured on the benchmark dataset (2 million users, 30 days, page views on most
user-days) it was 63 million rows after merging, not much smaller than the 100 million events it
summarised, because an active user touches most days. A 30-day retention matrix through the API took 3.9
seconds at the median, and the raw events 5.4. The rollup bought a quarter.

Profiling showed two costs. Reading the rows was cheap. Joining cohorts to per-user arrays of return days
was not, and neither was the arithmetic on 256-bit values afterwards.

## Decision

The rollup, `user_event_months`, has one row per project, event, month and user, and the days of that month
the user did the event as a 32-bit mask:

```sql
days SimpleAggregateFunction(groupBitOr, UInt32)
```

It is filled by a materialized view on the fact table and combined by the `AggregatingMergeTree` with OR,
which is idempotent: a duplicate event, or a view firing twice for a replayed batch, sets a bit that is
already set (ADR 0002). On the benchmark dataset it is 10 million rows.

A retention query makes one pass. For each user it shifts each month's mask into the query's frame, where
bit i is day `from + i`, and ORs them into two masks: when they did the start event, restricted to the
cohort range, and when they did the return event. The user's cohort is the lowest start bit. Shifting the
return mask right by it gives a 64-bit mask where bit k means "came back k days after their first day", and
the matrix is a sum of those bits by cohort. There is no join and no per-user array.

| Version, same data, measured in ClickHouse | Time |
|---|---:|
| day rollup, cohort join to arrays of return days | 2.2 to 3.0 s |
| day rollup, per-user 256-bit masks | 2.2 s |
| month rollup, 256-bit masks throughout | 1.5 s |
| month rollup, cohort-relative 64-bit mask per user | 0.55 to 0.8 s |

Through the API the 30-day matrix went from 3.9 seconds to about 1 on a freshly loaded month, and lower once
merged (docs/benchmark-results). The raw-events path, kept for comparison and for `QueryTests`' check that
the two agree, uses the same masks built from `toDate(ts)`.

## Consequences

A query follows at most 63 days after the start day, the width of the per-user mask, and spans at most 256
days from the first cohort to the last day followed, the width of the frame. Both are checked on the
request with a reason.

A user belongs to one cohort: the first day in the range they did the start event. That is the common
definition, and it means retention is not double-counted for users who repeat the start event.

The rollup is 25 months of history at a tenth of the fact table's rows, so retention outlives the 90-day
raw retention by design.
