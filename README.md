# ReadOnlyDbMcp

An MCP server that gives agents **read-only** database access by construction. Agents never
submit SQL — they call structured tools with table names, column names, filters, and values,
and the server authors every statement itself. Mutations are not restricted; they are
**inexpressible**.

Supported engines: **SQL Server**, **PostgreSQL**, **MySQL/MariaDB** (.NET 10).

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

4. Register the server in your MCP client (details in the next section). For Claude Code
   it's one command from your project directory:

   ```
   claude mcp add readonly-db -- C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe --connections demo
   ```

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
       }
     }
   }
   ```

   Providers: `sqlserver` (aliases `mssql`), `postgres` (`postgresql`, `pg`),
   `mysql` (`mariadb`). Use `connectionStringEnv` to pull the secret from an environment
   variable instead of the file. Config path can be overridden with `--config <path>` or the
   `READONLYDB_CONFIG` environment variable.

2. Register the server in your MCP client. The entry is the same everywhere — a local
   command plus args — shown per harness below. Replace the exe path with your clone's
   `publish` output.

   **Copilot CLI** — add to `~/.copilot/mcp-config.json` (global) or `.github/mcp.json`
   (project):

   ```json
   {
     "mcpServers": {
       "readonly-db": {
         "command": "C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe",
         "args": ["--connections", "orders"]
       }
     }
   }
   ```

   **Cursor** — the same JSON in `.cursor/mcp.json` (project) or `~/.cursor/mcp.json`
   (global).

   **Claude Code** — one command from your project directory (or the same JSON in
   `.mcp.json` at the project root):

   ```
   claude mcp add readonly-db -- C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe --connections orders
   ```

   **Codex CLI** — one command (stored in `~/.codex/config.toml` under
   `[mcp_servers.readonly-db]`):

   ```
   codex mcp add readonly-db -- C:/path/to/readonly-db-mcp/publish/ReadOnlyDbMcp.exe --connections orders
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
| `read_rows` | Structured read: joins, filters, aggregates, groupBy, orderBy, limit/offset |
| `count_rows` | `COUNT(*)` with the same join/filter syntax |

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

Join `on` may be omitted when exactly one foreign key relates the tables (the server infers
it); otherwise pass `on: { "left": "alias.col", "right": "alias.col" }`. Filter ops: `=`,
`!=`, `<`, `<=`, `>`, `>=`, `in`, `not_in`, `like`, `not_like`, `is_null`, `is_not_null`
(AND-combined). Aggregates: `count`, `sum`, `avg`, `min`, `max` with `groupBy`. Responses
include the generated SQL for transparency, and a `truncated` flag when the row cap was hit.

## Intended workflow for mutations

The agent uses these tools to understand schema and data, then writes any
`INSERT`/`UPDATE`/`DELETE`/migration as **text output** for a developer to review and run
through the normal change process. Mutation SQL never touches this server.

## Adding an engine

Implement `Providers/IDbProvider.cs` (connection factory, session read-only statement,
privilege check, identifier quoting, limit syntax, type categorization, schema introspection)
and register it in `ConnectionRegistry.Providers`. The query builder and tool surface are
engine-agnostic.
