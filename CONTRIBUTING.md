# Contributing

## Getting set up

You need the .NET 10 SDK, Node 24 and Docker.

```bash
dotnet build Tallyhouse.slnx
dotnet run --project tests/Tallyhouse.UnitTests
dotnet run --project tests/Tallyhouse.IntegrationTests   # starts Postgres, Redis, Kafka and ClickHouse

cd web && npm ci && npm test && npm run type-check
```

`docker compose up -d --build` brings the whole thing up: collector on 5180, dashboard on 5181, ClickHouse's
HTTP port on 8123. The demo project's keys are in `compose.yaml`.

## Ground rules

**Warnings are errors**, in C# and in ESLint. CI runs `dotnet format whitespace` and `dotnet format style`
with `--verify-no-changes`, and Prettier's check on the dashboard.

**A change to a guarantee comes with the test that would fail without it.** The claims in the README are
the point of the project. If you touch deduplication, the ingest order, lateness, the funnel algorithm or
the retention masks, the pull request should include the test that proves the new behaviour. The funnel is
checked against ClickHouse's own `windowFunnel` and a plain C# reference; keep both comparisons passing.

**Queries must stay blind to duplicates.** Until ClickHouse merges them, duplicate rows are really in the
fact table. A new query has to use a measure a duplicate cannot move, or pay for `FINAL`, and a new rollup
may only hold sets, never counts (ADR 0002). `QueryTests` has a test that stops merges and writes every
event twice; add your query to it.

**Contract changes go through the document.** Changing a response or request type changes the OpenAPI
document. Regenerate it and the dashboard's types:

```bash
dotnet build src/Tallyhouse.Api -p:OpenApiGenerateDocuments=true
cd web && npm run contract && npm run type-check
```

CI does the same and fails if either file differs from what is committed.

**Nothing commercially licensed, at any depth.** `tools/Tallyhouse.LicenseAudit` reads the licence of every
NuGet package in the restored tree, and `web/scripts/licence-audit.mjs` does the same for npm. If either
fails, replace the dependency rather than extending the allow list.

**Packages are centrally managed.** NuGet versions live in `Directory.Packages.props`; npm versions are
pinned exactly in `web/package.json`.

## Measurements

Performance claims come with a number and the hardware it was measured on.

- `benchmarks/Tallyhouse.Benchmarks` measures the collector's CPU per event. CI runs it once with
  `--job Dry` to catch one that no longer compiles.
- `load/Tallyhouse.Load` drives a running stack: `seed` and `queries` for query latency over 100 million
  events, `soak` for sustained ingest, `exactly-once` and `sampling` for the counting proofs.

Results live in `docs/benchmark-results`. If a change moves one of them, rerun it and update the file in the
same pull request.

## Writing

Documentation is in English and explains why rather than restating the code. Comments earn their place by
saying something the code cannot: a trade-off, a failure that was hit, a reason the obvious approach lost.
