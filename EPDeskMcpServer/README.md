# EPDesk MCP Server

A Model Context Protocol server that lets Claude locate and retrieve the files
EPDesk has collected from company Windows machines.

It exposes **file metadata and downloads only**. It does not search or read what
is inside a document — the extraction surface is shelved, see
[Extraction](#extraction-is-shelved).

It is a **read-only view** over the same PostgreSQL database and Backblaze B2
bucket that `EPDeskServerApi` already owns. It never runs migrations and never
mutates ingestion data. Every exposed tool issues only `SELECT`s.

---

## Why a separate service

`EPDeskMcpServer` references `EPDeskServerApi` as a project rather than
re-declaring the schema, so the EF model, `AppDbContext`, and the Backblaze
client are shared. There is one definition of the data and no drift when a
migration lands.

Running it as its own Railway service keeps a long-running model session from
competing with agent uploads for the API's request pipeline, and lets the MCP
surface be scaled, restarted, and rate-limited on its own.

---

## Tools

| Tool | What it answers |
| --- | --- |
| `epdesk_list_documents` | "Find this file by name or path", "everything from this device", "the biggest files". The only way in. |
| `epdesk_get_document` | One document's full record and every stored version. |
| `epdesk_get_file_metadata` | Size, SHA-256, content types, B2 location, derivatives, and a **live** storage existence check. |
| `epdesk_get_download_url` | A time-limited private download link for the original file or a derivative. |
| `epdesk_read_file_content` | Raw bytes inline for text-like files and small binaries. |
| `epdesk_list_devices` | The agent fleet, with per-device document counts and volume. |
| `epdesk_list_uploads` | Raw ingestion rows from both pipelines — "did this file ever arrive". |
| `epdesk_get_storage_stats` | Corpus totals and breakdowns by type, device, department, source. |

Both ingestion sources share these tools: `automatic_upload` from the laptop
agent and `old_user_data` from the archive import. `sourceType` separates them.

Every list tool returns `totalCount` and `nextOffset` so a model can page
deliberately instead of guessing. Page sizes, text budgets, and inline download
sizes are all clamped server-side.

---

## Configuration

All settings can be supplied as environment variables using the ASP.NET Core
double-underscore convention (`Mcp__ApiKey`).

### Required

| Variable | Notes |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | The same PostgreSQL database `EPDeskServerApi` uses. On Railway, reference the Postgres service variable. |
| `B2__ServiceUrl`, `B2__Region`, `B2__BucketName`, `B2__KeyId`, `B2__ApplicationKey` | Same Backblaze credentials as the API. Read access is enough. |

### Optional

| Variable | Default | Notes |
| --- | --- | --- |
| `Mcp__EnableWriteTools` | `false` | Enables `epdesk_requeue_extraction`, which is currently off the surface entirely. |
| `Mcp__DefaultPageSize` | `20` | Page size when a tool call omits `limit`. |
| `Mcp__MaxPageSize` | `100` | Hard ceiling on `limit`. |
| `Mcp__MaxTextCharacters` | `40000` | Ceiling on text returned by one `epdesk_read_document` call. Inactive while extraction is shelved. |
| `Mcp__MaxInlineDownloadBytes` | `1048576` | Ceiling on bytes returned by `epdesk_read_file_content`. |
| `Mcp__DownloadUrlMinutes` | `15` | Default presigned URL lifetime. |
| `PORT` | `8080` | Set automatically by Railway. |

### Authentication

There is none. The MCP endpoint accepts any request that reaches it, which was
a deliberate deployment decision — see [Exposure](#exposure).

---

## Deployment

The service is live in the `laptop-data-api` Railway project as **`epdesk-mcp`**:

```
https://epdesk-mcp-production.up.railway.app/mcp
```

Its settings are already configured: config-as-code points at
`EPDeskMcpServer/railway.json`, the health check is `/health/ready` with a
300 s timeout, and the restart policy is `ALWAYS`.

`/health/ready` verifies both PostgreSQL and Backblaze, so a deploy that cannot
reach either fails its health check rather than serving broken tools.

### Redeploying

The service is deployed by CLI upload rather than from GitHub, so a `git push`
does **not** redeploy it. To ship a change:

```powershell
railway up --service epdesk-mcp --ci
```

Run it from the repository root — the Dockerfile needs that as its build
context because it compiles two projects. `.railwayignore` keeps the upload to
source only.

To switch to automatic deploys on push, connect the GitHub repo in the Railway
dashboard (**Settings → Source**) and leave **Root Directory** empty. The
config-as-code path already handles the Dockerfile and health check.

### Recreating from scratch

1. `railway add --service epdesk-mcp`
2. Set the variables listed above. To avoid pasting the database password,
   reference the Postgres service instead:

   ```
   ConnectionStrings__DefaultConnection=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}};SSL Mode=Require;Trust Server Certificate=true
   ```

3. `railway up --service epdesk-mcp --ci`
4. `railway domain --service epdesk-mcp --port 8080`
5. Point **Config as code** at `EPDeskMcpServer/railway.json`.

---

## Connecting a client

The transport is **streamable HTTP, stateless**. No credentials are needed.

**Claude Code**

```bash
claude mcp add --transport http epdesk https://epdesk-mcp-production.up.railway.app/mcp
```

**Claude Desktop / any `mcpServers` config**

```json
{
  "mcpServers": {
    "epdesk": {
      "type": "http",
      "url": "https://epdesk-mcp-production.up.railway.app/mcp"
    }
  }
}
```

**MCP Inspector**

```bash
npx @modelcontextprotocol/inspector
```

Note that claude.ai custom connectors expect OAuth rather than a static bearer
token, so the header-based setup above covers Claude Code, Claude Desktop, and
IDE clients. Adding OAuth would mean fronting this service with an authorization
server; the SDK already ships `ModelContextProtocol.AspNetCore.Authentication`
for that if it is ever needed.

---

## Running locally

The database lives on Railway, so open a tunnel first:

```powershell
railway connect postgres --tunnel-only -P 55433
```

Then, from the repository root:

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=127.0.0.1;Port=55433;Database=railway;Username=postgres;Password=$env:RAILWAY_DB_PASSWORD;SSL Mode=Require;Trust Server Certificate=true"
$env:Mcp__ApiKey = "<32+ character key>"
$env:PORT = "8099"
dotnet run --project EPDeskMcpServer
```

Check it is alive:

```powershell
curl http://127.0.0.1:8099/health/ready
```

---

## Extraction is shelved

Full-text search, extracted-text reads, queue health, and requeue were removed
from the MCP surface. The corpus is served as metadata plus downloads while the
extraction pipeline is paused.

Nothing was deleted. `Tools/ExtractionTools.cs` holds all four tools intact and
compiles; it is simply not registered. `Program.cs` registers tool types
explicitly (`.WithTools<T>()`) rather than scanning the assembly, which is what
keeps that class off the wire.

To bring it back:

1. Add `.WithTools<ExtractionTools>()` to the builder chain in `Program.cs`.
2. Restore the extraction paragraphs in `ServerInstructions`.
3. Start the `epdesk-extraction-worker` Railway service so the queue drains —
   without it, jobs sit at `queued` and nothing becomes searchable.
4. For `epdesk_requeue_extraction`, also set `Mcp__EnableWriteTools=true`.

`epdesk_get_storage_stats` no longer reports extraction coverage, since with no
search surface a coverage percentage reads as "most files are unavailable" when
in fact every file is downloadable.

## Exposure

**The MCP endpoint is public and unauthenticated.** Anyone who reaches the URL
can search the full corpus, read any extracted document, and mint presigned
download links for any stored file — invoices, purchase orders, client
proposals, and personal documents belonging to named employees. The URL is the
only thing limiting access, and Railway subdomains are enumerable.

If that stops being acceptable, the cheapest fix is to put the key check back:
restore `Security/McpApiKeyMiddleware.cs`, re-add `ApiKey` to
`EpDeskMcpOptions`, and wire it with `app.UseWhen(...)` ahead of `MapMcp`. It is
a small, self-contained change.

## Safety properties that do still hold

- **Read-only.** All eight exposed tools issue only `SELECT`s. The one tool that
  could write, `epdesk_requeue_extraction`, is no longer registered at all, and
  would still refuse to run without `Mcp__EnableWriteTools`.
- **Storage credentials never leave the server.** Clients get presigned URLs
  scoped to a single object with a short expiry, never Backblaze keys.
- **Bounded responses.** Page sizes, text budgets, and inline byte limits are
  clamped server-side, so no single tool call can flood a model's context or
  pull a 700 MB deck through the connection.
- **Actionable errors.** Failures say what to do next rather than surfacing a
  stack trace, which is what lets a model recover on its own.
