# Ingest soak

```bash
dotnet run -c Release --project load/Tallyhouse.Load -- soak --rate 20000 --seconds 600 --batch 100
```

Twenty thousand events a second for ten minutes, 100 events per request, open loop: request i is due at
`i * 5 ms` whether or not earlier ones have answered, and its latency is measured from when it was due. At
the end the harness waits for the loader to drain and counts what ClickHouse holds.

The whole stack ran on one machine: Intel Core Ultra 7 255H, 16 cores, 32 GB, Windows 11, Docker Desktop
with 16 vCPUs and 15.3 GiB shared by every container. One collector, one loader, a single Kafka broker, a
single ClickHouse node. The generator ran in a container on the compose network, published with
`dotnet publish -r linux-x64` and started with `TALLYHOUSE_API=http://api:8080`.

| Measure | Ten minutes | One minute |
|---|---:|---:|
| Events acknowledged (202) | 12,000,000 | 1,200,000 |
| Achieved rate | 19,999 events/s | 19,998 events/s |
| Requests refused (503) | 0 | 0 |
| Requests failed (transport) | 0 | 0 |
| Ack latency p50 | 8.1 ms | 8.1 ms |
| Ack latency p95 | 19.1 ms | 11.0 ms |
| Ack latency p99 | 90.2 ms | 18.8 ms |
| Ack latency max | 1,652.4 ms | 48.6 ms |
| Peak requests in flight | 265 | 10 |
| Events in ClickHouse afterwards | 12,000,000 | 1,200,000 |
| Acknowledged but not stored | 0 | 0 |

Every acknowledged event was stored, and nothing was refused. The median is the same over one minute and
ten, but the tail is not. The spec asked for a p99 under 25 ms at this rate: the one-minute run meets it,
the ten-minute run does not. A peak of 265 requests in flight at 200 requests a second means the collector
stalled for more than a second at least once, and the p99 of 90 ms says it happened more than that.

I have not pinned the stalls on one cause. Over ten minutes on one machine the collector shares 16 vCPUs
with a Kafka broker rolling log segments, a loader writing 20,000 rows a second, ClickHouse merging the parts
that writing creates, and Redis holding a growing set of 12 million message ids, and any of them can take the
CPU for a second. Telling them apart needs the stalls timestamped against each container's CPU, which is the
next measurement, not a guess to write down here.

## Why the generator runs inside the Docker network

The first soak ran the generator on the Windows host, through Docker Desktop's published port, and reported
a p50 of 61 ms while a sequential client measured 11 ms. Two things were wrong with the measurement, not the
service:

- **`localhost` resolved to `::1` first.** .NET on Windows tries IPv6 before IPv4, Docker Desktop publishes
  the port on IPv4, and the failed attempt cost about 40 ms per request. `127.0.0.1` removed it: at 2,000
  events a second the p50 went from 62 to 12 ms.
- **The Windows timer and the port forwarder.** A dispatcher driven by `Task.Delay` fires on a 15.6 ms tick,
  so requests left up to that late and the latency included it; it now spins on a dedicated thread. What
  was left was a tail, p99 around 200 ms at only 20 requests a second, that a sequential Node client through
  the same port did not see as badly. From inside the compose network the tail was gone.

So the published number is the service's, measured next to it, and the host-side numbers are what a
developer poking at the stack from Windows will see. Both are real; only one is about the collector.

## What is being measured

An acknowledgement includes: parsing the batch, validating and normalising each event, one pipelined Redis
round trip to look for duplicates, producing to Kafka with `acks=all` and waiting for every delivery report,
and a fire-and-forget Redis write. The floor is the producer's `linger.ms` of 5, which trades a few
milliseconds per request for batching at the broker.
