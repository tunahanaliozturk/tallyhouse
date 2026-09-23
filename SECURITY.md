# Security

## Reporting a vulnerability

Please open a private security advisory on GitHub rather than a public issue. I will acknowledge it within a
week and say what I intend to do about it.

## What the design assumes

**Three credentials, each good for one thing.** A project write key (`thw_`) can only ingest. A project read
key (`thr_`) can only query and list. The operator token can change the catalog and replay quarantine. A
request to an endpoint with the wrong kind of credential gets 401, the same as no credential.

**Keys are stored as hashes.** A project's keys are 256 random bits, shown once when the project is created.
Postgres holds their SHA-256. The operator token is compared through SHA-256 digests with
`CryptographicOperations.FixedTimeEquals`, so neither its content nor its length leaks through timing.
Project keys are found by looking their hash up, which is safe for high-entropy keys: timing can at best
reveal a prefix of the hash of a guess.

**Write keys are not secrets in the way read keys are.** They ship inside client applications, so anyone
can extract one and send events to that project. What a write key cannot do is read anything back, change a
schema, or reach another project. Validation, quarantine and per-event limits bound what a hostile client
can store; rate limiting per key belongs in front of the collector.

**Personal data is redacted before it is written anywhere.** Properties named in a project's
`redactProperties` are replaced with `[redacted]` in the fact table and in quarantine alike, before either
write. Identifiers are treated as opaque and are not redacted: do not send e-mail addresses as user ids.

**Everything from a request that reaches SQL is a bound parameter.** The only query text built from input is
the shape of a query (how many funnel steps, which filter operators), and those come from validated counts
and enums. Partition names in the sessionizer are formatted from typed values, never from request strings.

**Limits at the edge.** 4 MiB per batch request and 500 events, 64 KiB for a single event, 64 properties per
schema, 1,024 characters per string property, 128 characters per message id, 256 per user id. Queries are
bounded in range, steps, filters and values, and ClickHouse is told to give up after 30 seconds.

**The dashboard holds no credential.** nginx adds the read key server-side and proxies only the read
endpoints. That makes the dashboard itself the access boundary for one project's analytics; put it behind
your own authentication (ADR 0009).

**Containers run as non-root.** The .NET images use the runtime image's app user.

## Not covered

- No per-key rate limiting in the collector. Put it in the gateway in front of it.
- No encryption of the fact table at rest beyond what the storage underneath provides. Redaction is how
  personal data is kept out of it.
- The demo keys in `compose.yaml` are public. The demo project exists only when `Tallyhouse:Demo:ProjectId`
  is set.
