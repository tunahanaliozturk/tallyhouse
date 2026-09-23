-- Seeds the benchmark dataset straight into the fact table.
--
-- This deliberately bypasses the collector. The query benchmark measures queries over 100 million rows, and
-- pushing 100 million events through HTTP and Kafka first would take an hour to measure something the ingest
-- soak already measures on its own. The rows are what the loader would have written: same columns, same
-- keys, same identity hash (ClickHouse's xxh3 is the XXH3_64 the collector computes), and the materialized
-- view builds the retention rollup from them exactly as it does from loaded batches.
--
-- Everything is derived from cityHash64 of the row number, so the dataset is identical on every run.
--
-- The shape, for {users} users over {days} days starting at {start}:
--   signup        every user, once, spread uniformly across the period
--   activate      45% of users, within 3 days of signing up
--   purchase      30% of activated users, one to three times, within 10 days of activating
--   page_view     {pageViews} views, users and times uniform across the period
--   feature_used  {features} events, the same way
INSERT INTO events (project_id, event_name, user_key, ts, message_id, user_id, anonymous_id, schema_version, properties, received_at, is_late, sample_threshold, sample_bucket)
SELECT
    {project:UUID},
    name,
    xxh3(concat('u', 'user-', toString(u))) AS key,
    ts,
    mid,
    concat('user-', toString(u)),
    '',
    1,
    props,
    ts,
    false,
    10000,
    key % 10000
FROM
(
    SELECT
        number AS u,
        'signup' AS name,
        {start:DateTime64(3, 'UTC')} + toIntervalMillisecond(cityHash64(number, 1) % (toUInt64({days:UInt32}) * 86400000)) AS ts,
        concat('su-', toString(number)) AS mid,
        map('plan', ['free', 'pro', 'team'][1 + cityHash64(number, 2) % 3]) AS props
    FROM numbers({users:UInt64})

    UNION ALL

    SELECT
        number,
        'activate',
        {start:DateTime64(3, 'UTC')} + toIntervalMillisecond(cityHash64(number, 1) % (toUInt64({days:UInt32}) * 86400000))
            + toIntervalMillisecond(cityHash64(number, 4) % 259200000),
        concat('ac-', toString(number)),
        map()
    FROM numbers({users:UInt64})
    WHERE cityHash64(number, 3) % 100 < 45

    UNION ALL

    SELECT
        number,
        'purchase',
        {start:DateTime64(3, 'UTC')} + toIntervalMillisecond(cityHash64(number, 1) % (toUInt64({days:UInt32}) * 86400000))
            + toIntervalMillisecond(cityHash64(number, 4) % 259200000)
            + toIntervalMillisecond(cityHash64(number, 7, k) % 864000000),
        concat('pu-', toString(number), '-', toString(k)),
        map('amount', toString(round(5 + (cityHash64(number, 8, k) % 20000) / 100, 2)),
            'currency', ['USD', 'EUR', 'TRY'][1 + cityHash64(number, 9, k) % 3])
    FROM numbers({users:UInt64})
    ARRAY JOIN range(1 + cityHash64(number, 6) % 3) AS k
    WHERE cityHash64(number, 3) % 100 < 45 AND cityHash64(number, 5) % 100 < 30

    UNION ALL

    SELECT
        cityHash64(number, 10) % {users:UInt64},
        'page_view',
        {start:DateTime64(3, 'UTC')} + toIntervalMillisecond(cityHash64(number, 11) % (toUInt64({days:UInt32}) * 86400000)),
        concat('pv-', toString(number)),
        map('path', ['/', '/pricing', '/docs', '/blog', '/signup', '/features', '/about', '/changelog'][1 + cityHash64(number, 12) % 8])
    FROM numbers({pageViews:UInt64})

    UNION ALL

    SELECT
        cityHash64(number, 20) % {users:UInt64},
        'feature_used',
        {start:DateTime64(3, 'UTC')} + toIntervalMillisecond(cityHash64(number, 21) % (toUInt64({days:UInt32}) * 86400000)),
        concat('fu-', toString(number)),
        map('feature', ['export', 'share', 'invite', 'search', 'filter', 'comment'][1 + cityHash64(number, 22) % 6])
    FROM numbers({features:UInt64})
)
