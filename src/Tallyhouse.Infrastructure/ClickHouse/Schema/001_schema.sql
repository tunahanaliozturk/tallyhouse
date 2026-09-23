-- The fact table. One row per accepted event, day-partitioned by the time the event says it happened.
--
-- Deduplication: the sorting key ends in message_id, so two deliveries of one event share a key and the
-- ReplacingMergeTree collapses them on merge. Until a merge runs both rows exist, which is why every query
-- that counts events is written to be indifferent to duplicates (see docs/adr/0002). arrival_rank decides
-- which copy survives: the one received first, because it is the copy whose lateness was judged first and
-- because it makes a query that skips FINAL and filters on is_late agree with one that uses it.
--
-- The key starts with event_name because every funnel, retention and segment query names its events, and
-- a funnel over three events then reads three narrow ranges instead of the whole day.
CREATE TABLE IF NOT EXISTS events
(
    project_id       UUID,
    event_name       LowCardinality(String),
    user_key         UInt64 CODEC(Delta, ZSTD(1)),
    ts               DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
    message_id       String CODEC(ZSTD(1)),
    user_id          String CODEC(ZSTD(1)),
    anonymous_id     String CODEC(ZSTD(1)),
    schema_version   UInt16,
    properties       Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    received_at      DateTime64(3, 'UTC') CODEC(ZSTD(1)),
    is_late          Bool,
    sample_threshold UInt16,
    sample_bucket    UInt16,
    arrival_rank     UInt64 MATERIALIZED toUInt64(4102444800000) - toUInt64(toUnixTimestamp64Milli(received_at))
)
ENGINE = ReplacingMergeTree(arrival_rank)
PARTITION BY toYYYYMMDD(ts)
ORDER BY (project_id, event_name, user_key, ts, message_id)
TTL toDateTime(ts) + INTERVAL 90 DAY
SETTINGS ttl_only_drop_parts = 1,
         -- A retried insert of an identical block, after a timeout that hid a success, is dropped here
         -- rather than waiting for a merge.
         non_replicated_deduplication_window = 1000;

-- The retention rollup: which users did which event on which day. It only ever holds a set, never a count,
-- and that is deliberate. A materialized view sees inserts, not merges, so a counting rollup would count
-- every duplicate the fact table has not merged away yet. A set is idempotent: inserting a member twice is
-- the same set.
CREATE TABLE IF NOT EXISTS user_event_days
(
    project_id       UUID,
    event_name       LowCardinality(String),
    day              Date,
    user_key         UInt64,
    sample_threshold UInt16,
    sample_bucket    UInt16
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(day)
ORDER BY (project_id, event_name, day, user_key)
TTL day + INTERVAL 730 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS user_event_days_mv TO user_event_days AS
SELECT
    project_id,
    event_name,
    toDate(ts) AS day,
    user_key,
    min(sample_threshold) AS sample_threshold,
    any(sample_bucket) AS sample_bucket
FROM events
WHERE NOT is_late
GROUP BY project_id, event_name, day, user_key;

-- Events that failed validation, with the reason, for somebody to fix and replay. A replay inserts the same
-- key with a newer updated_at and status 'replayed'.
CREATE TABLE IF NOT EXISTS quarantine
(
    project_id    UUID,
    received_at   DateTime64(3, 'UTC'),
    quarantine_id UUID,
    message_id    String,
    event_name    String,
    reason        String,
    payload       String CODEC(ZSTD(3)),
    status        Enum8('open' = 1, 'replayed' = 2),
    updated_at    DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(updated_at)
PARTITION BY toYYYYMM(received_at)
ORDER BY (project_id, received_at, quarantine_id)
TTL toDateTime(received_at) + INTERVAL 30 DAY;

-- Which (project, day) pairs received events since the sessionizer last looked. Written by the loader in
-- the same batch as the events, so a day cannot receive events without being marked.
CREATE TABLE IF NOT EXISTS session_dirty
(
    project_id UUID,
    day        Date,
    marked_at  DateTime64(3, 'UTC') DEFAULT now64(3)
)
ENGINE = MergeTree
ORDER BY marked_at
TTL toDateTime(marked_at) + INTERVAL 3 DAY;

-- Sessions, recomputed a whole (project, day) at a time and swapped in with REPLACE PARTITION, so a reader
-- sees either the old sessions of a day or the new ones and never half of each. Partitioning by project
-- is what makes the swap per project, and it is also this design's ceiling: partitions grow with projects
-- times retained days. See docs/adr/0004.
CREATE TABLE IF NOT EXISTS sessions
(
    project_id    UUID,
    day           Date,
    user_key      UInt64,
    user_id       String,
    session_start DateTime64(3, 'UTC'),
    session_end   DateTime64(3, 'UTC'),
    event_count   UInt32
)
ENGINE = MergeTree
PARTITION BY (project_id, day)
ORDER BY (user_key, session_start)
TTL day + INTERVAL 90 DAY
SETTINGS ttl_only_drop_parts = 1;

CREATE TABLE IF NOT EXISTS sessions_staging AS sessions;
