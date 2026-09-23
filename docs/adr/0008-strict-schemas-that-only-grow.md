# 8. Strict schemas that may only grow in place

Status: accepted

## Context

Most analytics data problems start as an instrumentation mistake nobody noticed for a month: a property
spelled `ammount`, a price sent as a string, a required field an SDK stopped sending. A permissive
collector stores all of it and the mistake surfaces in a dashboard, weeks later, as a number that is
slightly wrong.

## Decision

Every event is validated against a registered schema for its name and version before anything is stored.
Unknown properties are violations, not new columns, so a typo is caught on the first event. Types are
checked, required properties are required, and string properties may declare a closed set of values.

An invalid event is not dropped. It is written to the quarantine topic in the same Kafka append as the
valid events of its batch, so "acknowledged" means the same thing for every event, and it lands in a
`quarantine` table with the reason. Once the schema or the client is fixed, the operator replays it and it
is judged as of its original arrival time.

A version may change in place only by growing: new optional properties, new allowed values. Removing a
property, changing its type, flipping whether it is required, or narrowing its values is refused with a 409
that lists what would break and suggests the next version number. The rule keeps every stored event valid
and every written query correct.

Schemas live in Postgres, used through an EF Core `DbContext` directly. A registration reads the current
version, judges the change and saves; the row's `xmin` is the concurrency token, so two concurrent edits
judged against the same old spec cannot both save. A trigger bumps a revision number in the same
transaction as any catalog change. Every collector keeps the whole catalog in memory as a frozen snapshot
and polls that one number, so validation never touches the database and a Postgres outage stops catalog
changes, not ingestion.

A schema compiles to a frozen dictionary of property rules and a 64-bit mask of required properties, so
validating an event is one pass over its properties with no reflection.

## Consequences

A client that ships a new property before the schema is updated has those events quarantined until it is.
That is the point, and it is also friction; replay exists so it costs a replay and not the data.

A schema holds at most 64 properties, because the required mask is one `ulong`. A behavioural event with
more than that belongs in a different system.

A schema registered through one collector validates the next event sent to that collector immediately,
because the endpoint refreshes the cache before answering. Other collectors see it within the two-second
poll; an event reaching them in between is quarantined and can be replayed.

## Alternatives considered

**JSON Schema.** A standard, and a large one. The subset an analytics event needs is four types, required
flags and enums, and compiling that subset to a lookup is simpler and faster than a general validator.

**Additive evolution by accepting unknown properties.** It makes schema changes painless by making typos
invisible, which is the failure this decision is about.
