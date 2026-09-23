# Counting proofs

Both run against the compose stack through the public HTTP API, with the load harness on the same machine
(Intel Core Ultra 7 255H, 16 cores, 32 GB; Docker Desktop with 16 vCPUs and 15.3 GiB).

## Exactly once under duplicate delivery

```bash
dotnet run -c Release --project load/Tallyhouse.Load -- exactly-once --events 1000000 --duplicates 0.2 --restart-loader
```

A million events with a known answer: 100,000 users sign up, six in ten activate, four in ten of those
purchase, each step up to half an hour after the last, and page views fill the rest. Arrival order is
shuffled. 200,000 of the events are delivered a second time: half in a request sent at the same moment as
the original, so both race past the Redis layer, and half as retries in a later batch. The loader is
restarted halfway through, so the batch it was writing is delivered to ClickHouse again.

| Measure | Ground truth | Measured |
|---|---:|---:|
| distinct events stored | 1,000,000 | 1,000,000 |
| signup events | 100,000 | 100,000 |
| activate events | 60,032 | 60,032 |
| purchase events | 24,010 | 24,010 |
| page_view events | 815,958 | 815,958 |
| funnel step 1 (signup) users | 100,000 | 100,000 |
| funnel step 2 (activate) users | 60,032 | 60,032 |
| funnel step 3 (purchase) users | 24,010 | 24,010 |

What happened to the duplicates on the way:

- 101,001 deliveries were recognised by the collector and answered "duplicate" before reaching Kafka.
- 98,999 got past it, the concurrent twins, and were acknowledged and written to Kafka. That is the layer
  failing open exactly as designed (ADR 0002): it never refuses a first delivery, so it cannot stop two
  copies that arrive together.
- ClickHouse collapsed nearly all of those as it went: `ReplacingMergeTree` removes duplicates inside an
  insert block (`optimize_on_insert`) and between parts on merge. 81 were still physically present when the
  harness counted.
- Every query answered the ground truth anyway, because none of them can see a duplicate. The integration
  suite proves that part with merges stopped, so every duplicate is guaranteed to be present.

Delivering all 1.2 million copies took 22 seconds.

## Sampling accuracy

```bash
dotnet run -c Release --project load/Tallyhouse.Load -- sampling --users 200000
```

The same page views, one to nine per user, sent to a project that keeps everything and one that keeps
page views for 10% of users.

| Measure | Full count | Sampled estimate | Error |
|---|---:|---:|---:|
| page_view events | 998,781 | 1,006,050 | +0.73% |
| page_view users | 200,000 | 201,710 | +0.86% |

The sampled project stored 100,605 events, 10.07% of them. The spec's bound was 2%. The error is the
sampling error of which 10% of users were kept, not a bias: the estimator is Horvitz-Thompson over a
population that is kept or dropped whole (ADR 0007), and a unit test checks the kept fraction of 200,000
synthetic users is within four standard deviations of the rate.
