# 3. Events past the watermark are kept and labelled, never refiled

Status: accepted

## Context

A phone goes offline on a train and flushes three hours of events when it reconnects. Each event carries
the time it happened, and that is the time it should be counted at. But yesterday's daily numbers may
already be in a report, and silently growing them when a late batch arrives makes every published number
provisional forever.

The usual answer is a watermark: a point after which a time bucket is closed. The usual watermark is
event-time based, the newest timestamp seen minus an allowance. Here that is a trap. Clients set their own
timestamps, and one client with its clock a year ahead would advance the watermark for everybody and close
every bucket at once.

## Decision

The watermark moves with the collector's clock. An event is judged once, when it arrives:

| Condition | Outcome |
|---|---|
| more than 5 minutes in the future | quarantined, "timestamp is ahead of the collector clock" |
| older than 90 days | quarantined; the TTL would delete it on arrival |
| older than 2 hours | stored with `is_late = true` |
| otherwise | stored, on time |

Late events are stored and counted, just not in the closed buckets. The segment query reports them as
`lateEvents` beside the daily series. Funnels, retention and sessions leave them out, because those are
computed over closed days, and a late event changing a funnel that already appeared in a report is the
exact problem the watermark exists to prevent.

`is_late` is decided by the collector and travels with the record, so replaying a quarantined event later
judges it against its original arrival time, not the replay time.

## Consequences

Numbers for a day stop moving two hours after the day ends, and nothing is dropped to achieve that. The
dashboard shows how many events arrived late, so an analyst can see when an SDK is buffering too long.

Two hours is a trade. Longer means more late mobile traffic lands in its day and buckets stay provisional
for longer. It is a setting (`Tallyhouse:Ingestion:Watermark`).

A duplicate arriving on both sides of the watermark is stored twice with different `is_late` values until a
merge; the earliest copy wins the merge (ADR 0002), which is the on-time one.
