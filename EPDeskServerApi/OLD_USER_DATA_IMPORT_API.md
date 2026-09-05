# One-time Old User Data import

This API performs a one-time import without changing the normal EPDesk Agent
scan. The API host must be able to read the archive root directly, either as a
local folder or a read-only mounted volume.

The workflow is:

1. Create one persisted import job.
2. Scan the configured root and insert allowed-file metadata into PostgreSQL.
3. After the scan completes, upload indexed files to Backblaze one at a time.
4. Persist multipart progress so a server restart can resume the current file.

Only extensions in the existing file-upload policy are indexed. Empty files
and allowed files above the policy size limit are recorded as `skipped`.
Completed objects use this layout:

```text
uploads/old-user-data/<top-level-user-folder>/<relative-path>
```

## Configuration

The feature is disabled by default. Configure it with deployment secrets or
environment variables:

```text
OldUserDataImport__Enabled=true
OldUserDataImport__ApiKey=<a-long-random-secret>
OldUserDataImport__RootPath=E:\Users Data
OldUserDataImport__SourceLabel=Old User Data
OldUserDataImport__ObjectKeyPrefix=uploads/old-user-data
```

If the API runs in Docker, mount the external folder read-only and configure
the container path instead:

```powershell
docker run --mount 'type=bind,source=E:\Users Data,target=/imports/users-data,readonly' `
  -e OldUserDataImport__Enabled=true `
  -e OldUserDataImport__RootPath=/imports/users-data `
  -e OldUserDataImport__ApiKey=$env:OLD_USER_DATA_IMPORT_KEY `
  <other-existing-options> <image>
```

Deploy the API and database migration before enabling or starting the import.
Keep the drive attached until the job reaches `completed` or
`completed_with_errors`.

## Start once

All endpoints require the `X-Import-Key` header. `start` is idempotent for the
configured root: repeated calls return the original job and do not create
duplicate rows or uploads.

```powershell
$headers = @{ 'X-Import-Key' = $env:OLD_USER_DATA_IMPORT_KEY }

$job = Invoke-RestMethod `
  -Method Post `
  -Uri 'https://laptop-data.excellentpublicity.co/api/admin/old-user-data/start' `
  -Headers $headers
```

## Monitor

```powershell
$jobId = $job.id

Invoke-RestMethod `
  -Uri "https://laptop-data.excellentpublicity.co/api/admin/old-user-data/$jobId" `
  -Headers $headers

Invoke-RestMethod `
  -Uri "https://laptop-data.excellentpublicity.co/api/admin/old-user-data/$jobId/users" `
  -Headers $headers

Invoke-RestMethod `
  -Uri "https://laptop-data.excellentpublicity.co/api/admin/old-user-data/$jobId/files?status=uploading&take=200" `
  -Headers $headers
```

Job states are `pending`, `scanning`, `uploading`, `paused`, `completed`,
`completed_with_errors`, and `failed`. File states are `indexed`, `uploading`,
`completed`, `failed`, `missing`, and `skipped`.

## Pause, resume, and retry failures

Pause takes effect after the current scan batch or upload operation:

```powershell
Invoke-RestMethod -Method Post `
  -Uri "https://laptop-data.excellentpublicity.co/api/admin/old-user-data/$jobId/pause" `
  -Headers $headers

Invoke-RestMethod -Method Post `
  -Uri "https://laptop-data.excellentpublicity.co/api/admin/old-user-data/$jobId/resume" `
  -Headers $headers
```

Resuming a `completed_with_errors` job requeues failed and missing files while
leaving completed files unchanged.

## Browse and download

Search metadata:

```http
GET /api/admin/old-user-data/{jobId}/files?userFolder=Abhay%20Pandey&search=proposal&take=200
```

Create a temporary private Backblaze download URL:

```http
GET /api/admin/old-user-data/files/{fileId}/download
```
