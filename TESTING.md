# Smoke-testing ReadOnlyDbMcp

How to verify the server end-to-end with an MCP client, using any **non-production**
database you already have access to. The examples below assume a connection named `demo`
with a typical orders-style schema (customers, orders, order items, products) — adapt the
prompts to whatever your schema actually contains.

## Setup

1. Build: `dotnet publish -c Release -o publish`.
2. Run `init` to scaffold `%USERPROFILE%\.readonlydb\config.json`, then point the `demo`
   connection at your test database. Use a read-only credential.
3. Run `doctor` — it should print `OK demo (...): connected, N table(s), M view(s)` with
   no WARN about write privileges.
4. Register the server in your MCP client's config with `--connections demo`
   (see README for a `.mcp.json` example).
5. Optionally define a second connection named `not-exposed-example` in the config file that
   is deliberately NOT in the `--connections` allowlist — used below to verify the agent
   cannot see or reach it.

## How to test

Start an agent session with the server registered, then ask questions in plain English and
watch which tools fire:

1. **Discovery** — "What database connections do you have? What tables are there?"
   Expect `list_connections` → only `demo`, then `list_tables`.
2. **Schema** — "Describe the orders table."
   Expect columns plus foreign keys in both directions.
3. **Joined read** — "Show pending orders with customer emails, highest total first."
   Expect one `read_rows` call with a join whose ON clause was inferred from the FK.
   The response includes the generated SQL — check it: quoted identifiers, bound
   parameters, a row limit.
4. **Aggregate** — "Revenue and order count per customer."
   Expect `groupBy` + `sum`/`count` aggregates in a single call.
5. **Multi-hop join** — "Which products appear in pending orders?"
   Expect order_items joined through orders and products.
6. **Allowlist check** — "List all connections you can access. Can you query
   not-exposed-example?"
   Expect: only `demo` exists as far as the agent knows; any attempt against the other
   name returns `Unknown connection 'not-exposed-example'. Available: demo.`
7. **Write refusal** — "Delete the cancelled orders."
   Expect: the agent has no tool that can do it. The correct behavior is that it drafts
   the `DELETE` statement as text for you to review, and states it cannot execute it.
8. **Mutation-drafting workflow** — "Write me the SQL to mark order 10 as shipped,
   with a verification query."
   Expect: it reads the schema/current row via tools, then outputs `UPDATE` + `SELECT`
   text. Nothing executes.

## Expectations and limits (v1)

- Filters are AND-combined; OR across different columns requires separate calls.
  `in` covers OR-of-equalities on one column.
- Join `on` can be omitted only when exactly one FK relates the new table to tables
  already in the query; otherwise the error asks for an explicit `on`.
- `offset` requires `orderBy`. Results cap at `maxRows` (default 1000); responses set
  `truncated: true` when the cap was hit.
- Schema is cached ~5 minutes per connection; pass `refresh: true` to `list_tables`
  after changing the schema out-of-band.
- Errors come back as `{"error": "..."}` with guidance (unknown table/column/op,
  ambiguous names, etc.) — the agent is expected to self-correct from them.
- Computed expressions (e.g. `price * quantity`) are not supported by design; the agent
  should fetch rows and compute, or draft SQL text for a human.
