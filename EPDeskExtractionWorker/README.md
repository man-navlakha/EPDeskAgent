# EPDesk Extraction Worker

This Railway service runs the trusted orchestration side of document extraction. It:

- atomically claims leased extraction jobs from PostgreSQL;
- downloads an immutable private Backblaze B2 object and verifies its size, version,
  ETag, and SHA-256 identity;
- streams the temporary source file to the private extraction sandbox;
- writes a structured JSON derivative to B2 and searchable sections to PostgreSQL;
- releases, retries, rejects, or completes the job using its lease token; and
- deletes its per-job temporary directory on every exit path.

Malware scanning, MIME inspection, archive parsing, and format-specific extraction
run only in `EPDeskExtractionSandbox`. The credential-bearing worker does not invoke
document parsers, and its image does not contain ClamAV, Poppler, or Tesseract.

The worker also:

- verifies PostgreSQL, B2, and (when processing is enabled) sandbox readiness through
  `/health/ready`;
- exposes `/health/live` for process liveness;
- defaults to `ProcessingEnabled=false`; and
- supports an exact `CanaryJobId` plus a one-job process lifetime limit for rollout.

## Local configuration

Copy `.env.example` to an untracked `.env` file and replace its example values.
Environment variables use the same `ConnectionStrings__DefaultConnection` and
`B2__*` names as `EPDeskServerApi`. When processing is enabled,
`ExtractionWorker__SandboxBaseUrl` must be HTTPS, loopback HTTP, or a Railway
`.railway.internal` HTTP address, and `ExtractionWorker__SandboxApiKey` must match
the sandbox shared key and contain at least 32 non-whitespace characters.

Build the service:

```powershell
dotnet build .\EPDeskExtractionWorker.csproj
```

Run it after loading the required environment variables:

```powershell
dotnet run --project .\EPDeskExtractionWorker.csproj
```

Endpoints:

```text
GET /health/live   Process is running
GET /health/ready  PostgreSQL, B2, and enabled processing dependencies are ready
GET /              Runtime/canary status; contains no secret values
```

## Railway

Deploy this directory as the service root. Do not add a persistent volume; the
worker uses Railway's ephemeral filesystem only for per-job temporary files. The
worker and sandbox should communicate over Railway's private network and neither
service needs a public domain. Give the worker only PostgreSQL plus restricted B2
read/write credentials. Give the sandbox no PostgreSQL or B2 credentials.

Keep processing disabled until migrations are applied and one trusted source job
has an exact B2 version ID and SHA-256 checksum. For the first canary, configure one
replica, `MaxConcurrentJobs=1`, `MaxJobsPerInstanceLifetime=1`, and the exact
`CanaryJobId` before setting `ProcessingEnabled=true`.
