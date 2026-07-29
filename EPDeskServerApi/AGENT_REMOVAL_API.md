# EPDesk Agent Removal API

This document explains how to remotely uninstall every MSI installation whose
Windows display name is exactly `EPDesk Agent`.

## Important warning

This operation removes the Agent itself. After successful removal:

- The device stops sending heartbeats.
- Remote commands, scans, and uploads stop working.
- The device appears offline in the admin API.
- Restoring the Agent requires installing it again on the device.

The command removes matching installed applications only. It does not delete
the device record from the server database, and it does not explicitly delete
files under `C:\ProgramData\EPDeskAgent`.

## Requirements

Before using the removal endpoint:

1. Deploy the updated `EPDeskServerApi`.
2. Build and deploy EPDesk Agent version `1.0.13` to the target device.
3. Confirm that the device heartbeat reports version `1.0.13`.
4. Configure a secure removal API key on the server.

Older Agent versions do not understand the `REMOVE_EPDESK_AGENT` command. If
the command is sent to an older version, it can remain in the
`sent_to_agent` status without removing the application.

## Configure the removal API key

Set the following environment variable on the API server:

```text
Security__AgentRemovalApiKey=use-a-long-random-secret
```

For local PowerShell testing:

```powershell
$env:Security__AgentRemovalApiKey = "use-a-long-random-secret"
dotnet run --project .\EPDeskServerApi\EPDeskServerApi.csproj
```

For production, configure the same environment variable in the server,
container, or hosting platform. Restart the API after changing it.

Do not commit the real key to `appsettings.json` or source control. Use HTTPS
when calling the production API.

## Queue the removal command

```http
POST /api/admin/remote-commands/remove-epdesk-agent
Content-Type: application/json
X-Agent-Removal-Key: use-a-long-random-secret
```

Request body:

```json
{
  "deviceCode": "DEVICE-CODE",
  "requestedBy": "admin@example.com",
  "confirmation": "REMOVE EPDesk Agent"
}
```

The confirmation value is case-sensitive and must be exactly:

```text
REMOVE EPDesk Agent
```

### PowerShell example

```powershell
$apiBaseUrl = "https://laptop-data.excellentpublicity.co"
$removalKey = "use-a-long-random-secret"
$deviceCode = "DEVICE-CODE"

$headers = @{
    "X-Agent-Removal-Key" = $removalKey
}

$body = @{
    deviceCode = $deviceCode
    requestedBy = "admin@example.com"
    confirmation = "REMOVE EPDesk Agent"
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri "$apiBaseUrl/api/admin/remote-commands/remove-epdesk-agent" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $body
```

### curl example

```bash
curl --request POST \
  "https://laptop-data.excellentpublicity.co/api/admin/remote-commands/remove-epdesk-agent" \
  --header "Content-Type: application/json" \
  --header "X-Agent-Removal-Key: use-a-long-random-secret" \
  --data '{
    "deviceCode": "DEVICE-CODE",
    "requestedBy": "admin@example.com",
    "confirmation": "REMOVE EPDesk Agent"
  }'
```

### Successful queue response

The API returns `202 Accepted`:

```json
{
  "success": true,
  "message": "Removal queued. The device will uninstall every MSI registered with the exact display name 'EPDesk Agent'.",
  "id": "4d3836c8-524d-4e92-b12e-f01d0fb41cd9",
  "deviceCode": "DEVICE-CODE",
  "commandType": "REMOVE_EPDESK_AGENT",
  "status": "pending",
  "requestedAtUtc": "2026-07-29T10:00:00Z"
}
```

Save the returned `id` when it is necessary to identify this specific command.

## Check command status

```http
GET /api/admin/remote-commands?deviceCode=DEVICE-CODE&take=10
Accept: application/json
```

PowerShell:

```powershell
$apiBaseUrl = "https://laptop-data.excellentpublicity.co"
$deviceCode = "DEVICE-CODE"

Invoke-RestMethod `
    -Method Get `
    -Uri "$apiBaseUrl/api/admin/remote-commands?deviceCode=$deviceCode&take=10"
```

Possible statuses:

| Status | Meaning |
|---|---|
| `pending` | The server queued the command, but the device has not collected it. |
| `sent_to_agent` | The Agent collected the command and is starting cleanup. |
| `completed` | The detached cleanup helper completed the MSI removals. |
| `failed` | The command or one of the MSI uninstall operations failed. |

Agents poll for commands every 30 seconds by default. An offline device keeps
the command in `pending` until the Agent connects.

## How the cleanup works

When version `1.0.13` receives the command, it:

1. Searches both 32-bit and 64-bit machine uninstall registry views.
2. Selects only entries whose `DisplayName` exactly matches `EPDesk Agent`,
   ignoring letter case.
3. Collects and de-duplicates their MSI product codes.
4. Starts a detached PowerShell cleanup helper.
5. Stops the running Agent so Windows Installer can remove it.
6. Runs silent MSI uninstall for every matching product code.
7. Reports `completed` or `failed` to the server.

If two Agent copies start removal simultaneously, the helper retries Windows
Installer busy error `1618`. Duplicate uninstall attempts are treated
idempotently when Windows reports that a product is already absent.

Only MSI installations are removed. A non-MSI application with the same
display name is logged but not executed because arbitrary uninstall commands
are intentionally not run.

## API errors

| HTTP status | Cause |
|---|---|
| `400 Bad Request` | Device code is missing or the confirmation text is incorrect. |
| `401 Unauthorized` | `X-Agent-Removal-Key` is missing or incorrect. |
| `404 Not Found` | The device code does not exist in the server database. |
| `409 Conflict` | An active removal command already exists for the device. |
| `503 Service Unavailable` | `Security:AgentRemovalApiKey` is not configured on the server. |

If the command status is `failed`, inspect its `errorMessage` value through the
command-status endpoint. Common causes include:

- No exact-name MSI registration was found.
- The Agent service account does not have administrator permission.
- Windows Installer returned an error.
- The cleanup helper could not contact the server after uninstalling.

## Reinstalling the Agent

After a successful removal, reinstall the latest EPDesk Agent MSI manually or
through the organization's software deployment system. Confirm that only one
`EPDesk Agent` entry appears in Windows Installed Apps and that only one Agent
process or service is running.
