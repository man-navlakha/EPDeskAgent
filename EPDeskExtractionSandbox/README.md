# EPDesk extraction sandbox

This is an isolated HTTP extraction service. It has no database or B2 packages, configuration, or credentials. It reuses only the worker's format extractors as linked source files.

## Security boundary

- A shared API key is required for `POST /extract` and compared in fixed time.
- The raw body is streamed to a randomized, size-bounded temporary file.
- ClamAV readiness parses the daemon's `VERSION` response and rejects an unsupported engine, missing database version, or stale loaded-signature timestamp. A database file's writable mtime is never treated as proof that the running engine reloaded it.
- The signature database is loaded once into persistent `clamd` memory. Each request uses bounded `INSTREAM` frames over a group-restricted mode-0660 Unix socket instead of starting `clamscan` and loading signatures again.
- ClamAV scans the file before MIME inspection or any Office ZIP/XML parsing.
- The detected MIME type must agree with the safe file extension and declared `Content-Type`.
- Output size, archive expansion, XML, text, tables, external processes, and total request time are bounded.
- Temporary files are deleted in `finally`, including failed requests.
- A root PID 1 performs only validation, ownership setup, privilege dropping, and bounded process supervision. The HTTP/extraction workload runs as non-root `app`; `clamd` and FreshClam run as a separate non-root `clamav` identity with sanitized environments.
- The application cannot write the root-owned binaries, ClamAV database, daemon configuration, socket directory, PID file, or scanner temporary directory. It can only connect through the `epdesk-scan` socket group.
- One extraction runs at a time by default; concurrent requests receive HTTP 429.
- The entrypoint gives children 20 seconds to stop and then kills them; it stops the container if any supervised process exits so Railway can replace it.

Do not attach PostgreSQL, B2, or other application secrets to this Railway service. The worker should download and hash an object, call this service, then store the returned result itself.

## Railway setup

Create a separate Railway service from the repository root. Point its config file at `EPDeskExtractionSandbox/railway.json`; the Docker build context must remain the repository root because the project links `EPDeskExtractionWorker/Services/Extraction/*.cs`.
`Dockerfile.dockerignore` exposes only that extraction source directory from the worker to the sandbox build.

Set only a random service-specific secret with at least 32 characters:

```text
Sandbox__SharedApiKey=<random-secret>
```

Do not reuse a B2 key, database password, application admin key, or public client credential. Generate a separate secret and configure the same value only on the calling worker as its sandbox API key.

Optional policy variables use normal .NET nesting, for example:

```text
Sandbox__AllowImageOcr=true
Sandbox__AllowSpeakerNotes=true
Sandbox__MaxInputBytes=26214400
Sandbox__MaxConcurrentRequests=1
```

ClamAV limits are generated from the same bounded application settings at startup:

```text
Sandbox__MalwareScanTimeoutSeconds=120
Sandbox__ClamDaemonConnectTimeoutSeconds=5
Sandbox__MinimumClamVersion=1.4.5
Sandbox__MaxClamSignatureAgeHours=72
```

`MaxInputBytes` controls the HTTP body limit, ClamAV `StreamMaxLength`,
`MaxFileSize`, and `MaxScanSize`. `MaxArchiveEntries` also controls ClamAV
`MaxFiles`. ClamD receives one additional bounded thread so health probes are not
starved by the configured extraction concurrency. Container paths are intentionally restricted to `/var/lib/clamav`,
`/run/clamav`, and `/tmp`; do not override them on Railway.

The image installs the official ClamAV release package with a pinned SHA-256 and
fails at startup when the installed engine is below
`MinimumClamVersion`, and readiness independently checks the engine version loaded
by the daemon. Rebuild regularly and raise this minimum when the supported patch baseline changes;
do not run a production backlog on an end-of-life or known-outdated engine.

## Request

Send the file as the raw request body:

```bash
curl --fail-with-body \
  -X POST "$SANDBOX_URL/extract" \
  -H "X-Extraction-Key: $SANDBOX_API_KEY" \
  -H "X-File-Name: example.pdf" \
  -H "Content-Type: application/pdf" \
  --data-binary @example.pdf
```

Optional boolean headers are `X-Include-Speaker-Notes`, `X-Include-Word-Comments`, and `X-Enable-Image-Ocr`. `X-Ocr-Language` defaults to `eng`. A request cannot enable an option disabled by server policy.

A successful response is bounded JSON:

```json
{
  "detected_content_type": "application/pdf",
  "extracted_document": {
    "format": "pdf",
    "sections": [],
    "metadata": {},
    "warnings": []
  }
}
```

Health endpoints:

- `GET /health/live` checks only the HTTP process.
- `GET /health/ready` sends framed `PING` and `VERSION` commands to the private
  daemon socket, then validates the loaded engine/database version and the loaded
  database build timestamp.

The initial FreshClam update is bounded to 240 seconds before `clamd` starts, and
FreshClam then remains active in daemon mode. Successful updates notify the existing `clamd` process to reload;
`SelfCheck` provides a second periodic reload path. If signatures are missing or
stale, the daemon/socket is unavailable, a scan times out, the stream limit is
exceeded, or the daemon returns an unknown result, extraction fails closed.

## Backlog throughput and Railway resources

This design removes the signature-load cost from every file in a sequential
backlog. Unix-socket setup and file streaming still happen per request, while the
large signature database stays resident and ClamAV's clean-result cache remains
available between requests.

Start with one sandbox replica and `MaxConcurrentRequests=1`, matching the worker's
single canary slot. `clamd` keeps a substantial signature database in RAM and the
default concurrent reload temporarily holds both old and new engines. ClamAV's
container guidance lists 3 GiB as the minimum and 4 GiB as preferred, so allocate
4 GiB to this combined scanner/extractor service and monitor peak usage before
raising concurrency. Each replica downloads and loads its own signatures after a
cold start, and readiness remains unhealthy until that completes. Do not add a
public domain, persistent application volume, database variables, or B2 variables.

## Local build

Build the project directly from the repository root:

```bash
dotnet build EPDeskExtractionSandbox/EPDeskExtractionSandbox.csproj
dotnet test EPDeskExtractionSandbox.Tests/EPDeskExtractionSandbox.Tests.csproj
docker build -f EPDeskExtractionSandbox/Dockerfile .
```

The .NET tests validate protocol framing, response bounds, readiness, configuration
limits, and fail-closed verdict mapping without requiring ClamAV. A Docker smoke
test is still required before production because it verifies Debian package paths,
the separate non-root identities/socket permissions, FreshClam notification, and
an actual EICAR test scan. The smoke gate must also put EICAR after more than the
configured `MaxFiles` entries in a synthetic archive. `AlertExceedsMax` does not
document `MaxFiles` as an alerting case; do not enable real uploads if clamd returns
clean for that fixture. Never send a real uploaded document until all smoke checks
pass.
