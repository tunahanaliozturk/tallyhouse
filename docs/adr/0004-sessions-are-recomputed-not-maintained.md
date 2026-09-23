# 4. Sessions are recomputed per day and swapped in, not maintained incrementally

Status: accepted

## Context

A session is a user's run of events with no gap longer than thirty minutes. Computing that as events
stream past means keeping per-user state: the last event time and the open session. That state has to
survive restarts, be partitioned the way Kafka is, and be corrected when a late event arrives inside the
watermark and turns out to sit between two sessions, merging them, or before a session, extending it
backwards. Every one of those is a place for state to go wrong quietly.

## Decision

A session is a function of one user's events on one UTC day, so it is computed from those events rather
than maintained. Sessions end at midnight UTC, as Google Analytics has always cut them, which is what makes
a day an independent unit of work.

The loader writes a marker `(project, day)` into `session_dirty` in the same batch as the events, before
committing offsets. The sessionizer periodically reads markers newer than its cursor and, for each
dirty day, rebuilds that project's sessions for that day with window functions into a staging table, then
swaps the partition in with `REPLACE PARTITION`. A reader sees the old sessions for a day or the new ones,
never half of each.

The cursor is taken from ClickHouse's clock (the same clock that stamps markers) minus five seconds, so a
marker whose insert commits late is not skipped. On start the cursor goes back three days, the markers'
TTL, so a restart recomputes a little too much and misses nothing.

Late events (ADR 0003) are left out, so a closed day's sessions do not change.

## Consequences

A late event inside the watermark that bridges two sessions merges them on the next pass, with no code
specific to that case. `QueryTests` writes such an event and checks the two sessions become one.

`sessions` is partitioned by `(project_id, day)`, which is what makes the swap per project. It is also the
ceiling of this design: partitions grow with projects times retained days, and ClickHouse does not want
tens of thousands of them. Past a few hundred projects the swap would move to a generation column and a
pointer table.

Run one sessionizer per deployment. Two are safe, because each pass recomputes whole days from the events,
but they race each other's swaps and waste the work.

A bug found while building it: the cursor was first read as a zone-less `DateTime64` and converted "to
UTC", which on a machine not set to UTC shifted it by the local offset and skipped every marker. It now
travels as Unix milliseconds.

## Alternatives considered

**Stateful stream processing in the loader.** Faster to see a session appear, and it needs checkpointed
state, rebalancing-aware ownership of users, and a correction path for late events. Recomputing a day of
one project's events is seconds of ClickHouse work and has none of that.

**Sessions at query time.** No state at all, and the window functions run over every event in the range
on every dashboard load.
