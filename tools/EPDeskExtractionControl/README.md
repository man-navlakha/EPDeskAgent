# EPDesk extraction control

This local operator command creates one idempotent extraction job for an exact
completed automatic-upload row. It never lists source names, paths, object keys,
or credentials.

Set `EPDESK_CONTROL_DATABASE` and `EPDESK_CONTROL_B2_BUCKET` only in the process
environment, then run:

```powershell
dotnet run --project tools/EPDeskExtractionControl -- `
  enqueue-automatic <source-record-id>
```

Use this only for an exact canary. Normal queue creation belongs in the API after
the agent upload authentication rollout is complete.

## Resumable bulk enqueue

The bulk mode selects completed `.pdf`, `.docx`, `.pptx`, `.txt`, and `.md`
sources whose sizes are between 1 byte and 25 MiB, inclusive. It skips an exact
source revision that already has a `v1` extraction job. Missing legacy B2 version
or ETag fields act as unknown values, so the worker can learn and persist those
identities without making the same source eligible again.

```powershell
dotnet run --project tools/EPDeskExtractionControl -- `
  enqueue-bulk --batch-size 250
```

Each batch is ordered by source size and then database ID, and is committed in
its own transaction. Automatic uploads and Old User Data sources share each
batch fairly. Progress output contains counts only; it never prints source,
document, version, or job IDs.

Stopping the command rolls back only the active batch. Running the same command
again safely resumes because previously committed `v1` jobs are excluded by the
next database query. The batch size defaults to 250 and cannot exceed 250.

For a large one-time production backlog, deploy the included Dockerfile as a
private Railway runner with `EPDESK_CONTROL_DATABASE` referencing the private
PostgreSQL URL and `EPDESK_CONTROL_B2_BUCKET` set to the existing bucket name.
Use an `ON_FAILURE` restart policy and no public domain. The process exits after
all currently eligible sources are queued; rerunning it remains safe.
