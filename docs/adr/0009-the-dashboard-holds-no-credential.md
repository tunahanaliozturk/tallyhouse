# 9. The dashboard holds no credential and cannot drift from the API

Status: accepted

## Context

The dashboard reads one project's data with that project's read key. A key in a browser is a key in
`localStorage`, in the bundle or in a request anyone can copy from the network tab, and a read key reads
everything in the project.

Separately, the dashboard and the API are written in different languages. Two hand-maintained descriptions
of the same response always drift, and the drift shows up as `undefined` three components away from the
fetch that received it.

## Decision

**No key in the browser.** The dashboard is served by nginx, which proxies the read endpoints it uses and
adds `Authorization: Bearer <read key>` itself, from its environment. Nothing in the bundle, the page or
browser storage is a credential. Only the four query endpoints, the schema list and the quarantine list are
proxied; ingest and the operator endpoints are not reachable through the dashboard whatever the browser
sends, and queries are rate limited per client before they reach the API. In development, the Vite proxy
does the same with a key from the developer's environment.

**One contract.** The API writes its OpenAPI document at build time, `openapi-typescript` generates types
from it, and every response schema the dashboard parses with Zod is declared `satisfies` the generated
type. A renamed or added response field fails the dashboard's type-check. CI regenerates the document from
the server, fails if it differs from the committed copy, regenerates the types and type-checks again, so a
server change that was not carried through to the dashboard fails the pull request that made it.

Responses are parsed, never cast. A response that does not match fails at the fetch with the path of the
field.

## Consequences

Anyone who can reach the dashboard can read that project's analytics. The dashboard is therefore the
boundary, and it belongs behind whatever authentication the organisation already uses for internal tools.
The README says this plainly.

The contract check found a real mismatch while it was being written: the schema list response had been
parsed with a looser type than the server sends, and the type-check refused it.

One direction slips past the type-check: a field the server removes that the dashboard still parses. The
parsed type then has a property the generated type lacks, which is structurally allowed. The runtime parse
catches it instead, at the fetch, with the field's path, and the dashboard journeys in CI run against the
real API.
