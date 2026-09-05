# Document extraction queue

Completed uploads are now converted into three durable records in the same
PostgreSQL transaction as their completion:

1. one canonical `Document` for the source upload row;
2. one immutable `DocumentVersion` for the exact B2 object revision; and
3. one queued `ExtractionJob` for pipeline `v1`.

Calling the completion endpoint again is safe. It repairs a missing document,
version, or job without creating a duplicate.

## Configuration

Set a dedicated secret on the API service. Do not place the value in
`appsettings.json` or commit it to source control.

```text
DocumentExtraction__AdminApiKey=<long-random-secret>
```

The backfill endpoint fails closed with HTTP 503 when this secret is absent.
Clients send the secret in `X-Document-Extraction-Key`.

## Backfill existing completed uploads

Endpoint:

```text
POST /api/admin/document-extraction/backfill
```

Start with a dry run:

```json
{
  "sourceType": "all",
  "batchSize": 100,
  "dryRun": true
}
```

Then enqueue the same batch:

```json
{
  "sourceType": "all",
  "batchSize": 100,
  "dryRun": false
}
```

Allowed source types are `all`, `automatic_upload`, and `old_user_data`.
`batchSize` must be between 1 and 500. Repeat non-dry-run calls until the
response returns `mayHaveMore: false`.

For the first production canary, target exactly one known source row. In this
mode `sourceType` cannot be `all` and `batchSize` must be `1`:

```json
{
  "sourceType": "automatic_upload",
  "sourceRecordId": "00000000-0000-0000-0000-000000000001",
  "batchSize": 1,
  "dryRun": true
}
```

Repeat the exact request with `dryRun: false` only after checking the candidate
count. Use the returned `jobIds[0]` as the worker's exact canary job ID.

Example PowerShell request:

```powershell
$headers = @{ "X-Document-Extraction-Key" = $env:DOCUMENT_EXTRACTION_ADMIN_KEY }
$body = @{
    sourceType = "all"
    batchSize = 100
    dryRun = $true
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri "https://<api-host>/api/admin/document-extraction/backfill" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $body
```

The response reports candidate and creation counts. A candidate is a completed
source revision missing any of its canonical document, immutable version, or
`v1` extraction job.

## Rollout safety

Apply the API migrations before enabling the worker. Run the dry run before
the write-mode backfill. The backfill only creates queue metadata; it does not
download files or start extraction until the Railway worker processing switch
is enabled.
