# 7. Sample users, not events, and scale each measure the way it can be scaled

Status: accepted

## Context

Page views are most of the volume and the least valuable events individually. Keeping one in ten of them
cuts storage and query cost for that type by nine tenths. The question is which one in ten.

A coin flip per event is unbiased for event counts and wrong for everything else. It keeps some of a
user's page views and drops others, so a funnel that starts at a page view loses users at random steps,
and a heavy user is almost certain to appear in the sample while a light one often vanishes, which biases
every per-user measure towards heavy users.

## Decision

Every user falls in one of 10,000 buckets, `xxh3(identity) % 10000`, computed once at ingest and stored on
the row. An event type sampled at rate r keeps the users in buckets below `r * 10000`, so a kept user is
kept for every event of that type and a dropped user for none.

The kept populations nest: a user kept at 10% is also kept at 50%. That is what lets a query mixing types
sampled at different rates restrict itself to the smallest population and scale by one factor.

The threshold in force when an event arrived is stored on the row, so changing a rate next week does not
change how last month is scaled. Each measure is scaled the way it can be:

| Measure | Estimator |
|---|---|
| Event counts | each kept event weighs `10000 / threshold` (Horvitz-Thompson) |
| Unique users, funnels, retention | restrict to buckets below the smallest threshold among the events involved, multiply by `10000 / that threshold` |

The response says when a number is an estimate and by what factor it was scaled, and the dashboard marks
estimated counts with "≈".

## Consequences

`SamplingAccuracy` in the load harness sends the same page views, one to nine per user, to a project that
keeps everything and one that samples at 10%, and compares what each reports (docs/benchmark-results).

Sampling a type that defines sessions changes sessions for the users outside the sample: their sessions
are stitched from the events that were kept. Sessions are not scaled. Do not sample the events that
sessions are built from.

The estimate is only as good as the hash is uniform. XxHash3 is, and a unit test checks that the kept
fraction over 200,000 synthetic users is within four standard deviations of the rate.

## Alternatives considered

**Sampling in ClickHouse with `SAMPLE BY`.** It samples at query time over data that was fully stored,
which saves query time and none of the storage, and the sampling expression has to be part of the primary
key, which the funnel layout has no room for.
