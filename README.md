# ReadOnlyDbMcp

An MCP server that gives agents **read-only** database access by construction. Agents never
submit SQL — they call structured tools with table names, column names, filters, and values,
and the server authors every statement itself. Mutations are not restricted; they are
**inexpressible**.

Supported relational engines: **SQL Server**, **PostgreSQL**, **MySQL/MariaDB**. It also
supports **SQL Server Analysis Services (SSAS) tabular models** through a distinct ADOMD
semantic-model path (.NET 10).

## Security model

1. **Structural guarantee (primary).** The only tool inputs are identifiers and values.
   Every identifier is validated against live introspected schema and quoted by the provider;
   every value becomes a bound parameter. No agent-supplied string is ever concatenated into
   SQL, so injection and write statements have no channel to exist in. The invariant to protect
   in code review: *no string field is ever spliced into statement text.*
2. **Engine-level read-only (defense in depth).** Postgres sessions run with
   `SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY`; MySQL with
   `SET SESSION transaction_read_only = 1`. Every query runs inside a transaction that is
   always rolled back. SQL Server has no session read-only mode — there, layer 1 plus a
   `db_datareader`-only credential carry the guarantee.
3. **Credential audit.** On first use of each connection the server checks the credential's
   privileges (sysadmin/superuser membership, write grants) and logs a warning to stderr if it
   can write. A wrong connection string degrades to a warning, not a write path — but use a
   read-only credential anyway.
4. **Connection exposure allowlist.** Connection strings live only in a local config file,
   outside any repo. A server instance serves **only** the connections named in its
   `--connections` argument — which belongs in your checked-in, code-reviewed MCP client config.
   Other connections in the config file are invisible: no tool lists them, and no tool ever
   returns a connection string.
5. **Blast-radius limits.** Server-side row cap (`maxRows`), default limit, command timeout,
   oversized cells truncated. The realistic failure mode left is an expensive scan, not a write.

## Quickstart

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

1. Clone and build once:

   ```
   git clone https://github.com/stvoorhees/readonly-db-mcp
   cd readonly-db-mcp
   dotnet publish -c Release -o publish
   ```

2. Scaffold the connection config, then edit it to point at your database with a
   **read-only credential**:

   ```
   publish\ReadOnlyDbMcp.exe init
   ```

3. Verify everything before involving an MCP client — connects, audits credential
   privileges, counts tables:

   ```
   publish\ReadOnlyDbMcp.exe doctor
   ```

4. Register the server in your MCP client — Copilot CLI, Cursor, Claude Code, and
   Codex CLI each have a snippet or one-liner under
   [Configuration](#configuration) below.

`init` and `doctor` are command-line verbs handled before the MCP server starts; they are
never exposed as MCP tools, so agents cannot invoke them.

## Configuration

1. `%USERPROFILE%\.readonlydb\config.json` holds all connections — `init` scaffolds it,
   and it must never be committed:

   ```json
   {
     "maxRows": 1000,
     "defaultLimit": 100,
     "commandTimeoutSeconds": 30,
     "connections": {
       "orders": {
         "provider": "sqlserver",
         "connectionString": "Server=...;Database=Orders;Integrated Security=true"
       },
       "analytics": {
         "provider": "postgres",
         "connectionStringEnv": "ANALYTICS_DB_CONNECTION"
       },
       "legacy": {
         "provider": "mysql",
         "connectionStringEnv": "LEGACY_DB_CONNECTION"
       },
       "semantic_model": {
         "provider": "ssas",
         "connectionStringEnv": "SEMANTIC_MODEL_CONNECTION"
       }
     }
   }
   ```

   Providers: `sqlserver` (aliases `mssql`), `postgres` (`postgresql`, `pg`),
   `mysql` (`mariadb`), and `ssas` (`tabular`). SSAS uses ADOMD and supports standard
   connection strings, including Windows integrated authentication. Use
   `connectionStringEnv` to pull the secret from an environment variable instead of the
   file. Config path can be overridden with `--config <path>` or the `READONLYDB_CONFIG`
   environment variable.

2. Register the server in your MCP client. The entry is the same everywhere — a local
   command plus args — shown per harness below. Replace the exe path with your clone's
   `publish` output, and make sure `--connections` names connections that exist in your
   `config.json` (the `init` template defines one named `demo`) — the server exits at
   startup otherwise, which MCP clients report as a closed connection.

   **Copilot CLI** — add to `~/.copilot/mcp-config.json` (global) or `.github/mcp.json`
   (project):

   ```json
   {
     "mcpServers": {
       "readonly-db": {
         "type": "stdio",
         "tools": ["*"],
         "command": "C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe",
         "args": ["--connections", "demo"]
       }
     }
   }
   ```

   **Cursor** — the same JSON in `.cursor/mcp.json` (project) or `~/.cursor/mcp.json`
   (global).

   **Claude Code** — one command from your project directory (or the same JSON in
   `.mcp.json` at the project root):

   ```
   claude mcp add readonly-db -- C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe --connections demo
   ```

   **Codex CLI** — one command (stored in `~/.codex/config.toml` under
   `[mcp_servers.readonly-db]`):

   ```
   codex mcp add readonly-db -- C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe --connections demo
   ```

   The project-scoped files are safe to commit — they name connections, never secrets —
   though the exe path is machine-specific, so each dev adjusts it (or you standardize a
   clone location). The server refuses to start if `--connections` is missing or names an
   undefined connection.

## Tools

| Tool | Purpose |
|---|---|
| `list_connections` | Names and provider kinds of exposed connections only |
| `list_tables` | Tables and views (`refresh: true` to reload; schema cached ~5 min) |
| `describe_table` | Columns plus foreign keys in both directions |
| `get_view_definition` | The SQL a view is defined as (opt-in per connection; see note below) |
| `read_rows` | Structured read: joins, filters, aggregates, groupBy, orderBy, limit/offset |
| `count_rows` | `COUNT(*)` with the same join/filter syntax |
| `list_tabular_tables` | Visible tables in an SSAS tabular model |
| `describe_tabular_table` | Visible columns, measures, and relationships for a tabular table |
| `read_tabular_rows` | Structured, capped row or measure read from a tabular model |

`read_rows` example — the agent sends only structure:

```json
{
  "connection": "orders",
  "from": "orders",
  "joins": [{ "table": "customers" }],
  "columns": ["orders.id", "orders.total", "customers.email"],
  "filters": [{ "column": "orders.status", "op": "=", "value": "pending" }],
  "orderBy": [{ "column": "orders.total", "dir": "desc" }],
  "limit": 50
}
```

**View definitions are opt-in.** `get_view_definition` is only offered when a served
connection sets `"exposeViewDefinitions": true` in the config file; when no connection
opts in (the default — the flag can be omitted entirely), the tool is not registered at
all, so agents never see it. The reason it's opt-in: reading a definition needs a metadata
privilege that plain read access doesn't include on some engines — `VIEW DEFINITION` on
SQL Server (not part of `db_datareader`) and `SHOW VIEW` on MySQL; Postgres needs nothing
extra. These are metadata-only grants: adding them to your read-only credential does not
weaken read-only enforcement. Enable the flag only where the credential has the grant —
`doctor` verifies this and fails with the exact missing privilege if it can't read a
definition on an opted-in connection.

Join `on` may be omitted when exactly one foreign key relates the tables (the server infers
it); otherwise pass `on: { "left": "alias.col", "right": "alias.col" }`. Filter ops: `=`,
`!=`, `<`, `<=`, `>`, `>=`, `in`, `not_in`, `like`, `not_like`, `is_null`, `is_not_null`
(AND-combined). Aggregates: `count`, `sum`, `avg`, `min`, `max` with `groupBy`. Responses
include the generated SQL for transparency, and a `truncated` flag when the row cap was hit.

### SSAS tabular models

SSAS is intentionally not exposed through relational tools: tabular metadata has tables,
columns, measures, and relationships rather than SQL schemas and foreign keys, and its query
language is DAX. Use `list_tabular_tables`, then `describe_tabular_table`, followed by
`read_tabular_rows`.

`read_tabular_rows` accepts only metadata-validated table, column, and measure names; typed
`=`/`in` filter values; an optional selected-field sort; and a bounded limit. The server builds
the DAX itself and does **not** accept raw DAX, MDX, XMLA, expressions, calculated columns, or
measure definitions. Selecting columns reads model rows. Including measures calculates them at
the selected-column granularity. SSAS metadata discovery uses fixed, bounded server-authored
DMV queries; measure expressions are never returned.

## Intended workflow for mutations

The agent uses these tools to understand schema and data, then writes any
`INSERT`/`UPDATE`/`DELETE`/migration as **text output** for a developer to review and run
through the normal change process. Mutation SQL never touches this server.

## Adding an engine

Implement `Providers/IDbProvider.cs` (connection factory, session read-only statement,
privilege check, identifier quoting, limit syntax, type categorization, schema introspection)
and register it in `ConnectionRegistry.Providers`. The query builder and tool surface are
engine-agnostic.

Semantic models are a separate integration boundary. Do not implement them through
`IDbProvider`, `QueryBuilder`, or `QueryExecutor`: add an ADOMD-backed registry, metadata cache,
and restricted server-side query builder analogous to the `Tabular` implementation.
