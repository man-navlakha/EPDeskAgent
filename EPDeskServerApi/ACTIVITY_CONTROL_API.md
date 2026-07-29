# Agent activity control API

These endpoints remotely enable or disable file scanning and automatic
Backblaze file uploads on one EPDesk Agent device.

The server queues a remote command. The device normally receives it within its
configured `Agent:CommandPollIntervalSeconds`, applies it, persists the setting
locally, and marks the command `completed`.

Stopping an activity does not stop the Windows service, heartbeats, update
checks, or remote-command polling.

## Endpoints

| Method | Endpoint | Command |
| --- | --- | --- |
| `POST` | `/api/admin/remote-commands/scan/start` | Enables scanning and requests an immediate scan. |
| `POST` | `/api/admin/remote-commands/scan/stop` | Disables scanning and cancels the active scan, if any. |
| `POST` | `/api/admin/remote-commands/file-upload/start` | Enables automatic uploads and requests an immediate upload pass. |
| `POST` | `/api/admin/remote-commands/file-upload/stop` | Disables automatic uploads and cancels the active automatic upload, if any. |

All four endpoints use the same JSON body:

```json
{
  "deviceCode": "PC-001",
  "requestedBy": "admin@example.com"
}
```

`deviceCode` is required and must identify a device already registered through
the heartbeat endpoint. `requestedBy` is optional audit information.

## Queue response

The endpoints return `202 Accepted` because applying the setting is
asynchronous:

```json
{
  "success": true,
  "message": "Scan stop command queued.",
  "command": {
    "id": "267acc58-6b16-454b-909b-437e6e3d4277",
    "deviceCode": "PC-001",
    "commandType": "STOP_SCAN",
    "status": "pending",
    "requestedBy": "admin@example.com",
    "requestedAtUtc": "2026-07-29T18:30:00Z"
  }
}
```

Submitting the same action again while an equivalent command is still
`pending` or `sent_to_agent` is idempotent: the API returns `202 Accepted` with
the existing command instead of creating a duplicate.

## Check command status

```http
GET /api/admin/remote-commands?deviceCode=PC-001&take=20
```

The normal lifecycle is:

```text
pending -> sent_to_agent -> completed
                            \-> failed
```

`sent_to_agent` means the command was delivered, not that the activity setting
has been applied. Treat `completed` as the acknowledgement from the device.

## Behavior and boundaries

- Scan STOP cancellation flows through directory enumeration and metadata
  synchronization. Data committed before cancellation remains valid.
- File-upload STOP affects the automatic Backblaze upload pipeline. It does not
  disable one-off `FileRequest` downloads requested by an administrator.
- Multipart uploads are resumable. Stopping leaves uploaded parts available so
  a later START can continue the upload.
- The ON/OFF state is stored at
  `%ProgramData%\EPDeskAgent\activity-control.json` by default, so it survives
  Windows service restarts.
- `Agent:ActivityControlStatePath` can override the state-file location.
- The server currently has no general admin authentication middleware. Protect
  these endpoints with the same admin authentication/authorization policy used
  by the management UI before exposing them publicly.

## Examples

Stop scanning:

```bash
curl -X POST "https://api.example.com/api/admin/remote-commands/scan/stop" \
  -H "Content-Type: application/json" \
  -d '{"deviceCode":"PC-001","requestedBy":"admin@example.com"}'
```

Start automatic uploads:

```bash
curl -X POST "https://api.example.com/api/admin/remote-commands/file-upload/start" \
  -H "Content-Type: application/json" \
  -d '{"deviceCode":"PC-001","requestedBy":"admin@example.com"}'
```
