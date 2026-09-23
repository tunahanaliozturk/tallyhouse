# 1. Acknowledge an event when it is durable, not when it is queryable

Status: accepted

## Context

A client sends an event and waits for an answer. Whatever that answer means is the contract the whole
pipeline has to keep. There are two obvious candidates for when to say 202:

- after the event is in ClickHouse, where queries can see it, or
- after the event is somewhere it cannot be lost from.

The first one ties ingest availability to the analytics store. ClickHouse merges, gets restarted for
upgrades, and occasionally falls behind under a heavy query. Every one of those would turn into refused
events at the client, and a mobile SDK that is refused either drops the event or grows an unbounded local
queue. The first one also makes ingest latency a function of insert batching, and ClickHouse wants few
large inserts, not one per request.

## Decision

The collector acknowledges once Kafka has the event: `acks=all` on an idempotent producer, so every
in-sync replica has written it and a broker retry cannot write it twice. A separate loader moves events
from Kafka into ClickHouse in large batches and stores its offsets only after the rows are in.

There is no queue in the collector's memory between the HTTP request and the producer. librdkafka already
holds a bounded queue and batches from it (`queue.buffering.max.messages`, `linger.ms`). A second queue in
front of it would add a place for acknowledged-looking events to disappear on a crash without adding any
property. When librdkafka's queue is full, produce fails at once and the request gets a 503 with
`Retry-After`, which is the back-pressure a client should see.

Readiness checks the catalog and the broker and nothing else. Redis and ClickHouse are deliberately absent:
taking the collector out of the load balancer because the analytics store is down would convert a query
outage into data loss at the client.

## Consequences

"Accepted" means "will be counted", not "is counted". Freshness is the loader's batch delay plus
ClickHouse's insert time, about a second in the default configuration. The dashboard says so.

A ClickHouse outage costs freshness and nothing else. `ChaosTests` pauses the ClickHouse container,
sends 1,000 events that are all acknowledged, and checks all 1,000 arrive once it is back. Kafka keeps a
week of retention, which is the longest store outage the pipeline survives without loss.

A Kafka outage stops acknowledgements. `ChaosTests` pauses the broker and checks the collector answers 503
rather than 202, then that the retried batch is stored exactly once.

The loader delivers at least once. A crash between the insert and the offset commit replays the batch,
which is harmless only because the fact table and everything derived from it is idempotent
(ADR 0002).

## Alternatives considered

**Write to ClickHouse from the collector, with async inserts.** ClickHouse can buffer small inserts
itself. It removes Kafka from the stack, and it makes the acknowledgement depend on the store being up,
which is the property this decision exists to avoid.

**An in-process channel with a background producer.** It lets the request return before Kafka has the
event, which is faster and is a lie: the acknowledgement would survive a process crash that the event
does not.
