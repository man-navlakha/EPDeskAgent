# File Upload Administration API

This document covers the file-upload policy endpoints used by agents and
administrators, and the administrative upload-list endpoint.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/agent/file-uploads/policy` | Read the effective upload policy from an agent. |
| `GET` | `/api/admin/file-upload-policy` | Read the effective upload policy from an admin client. |
| `PUT` | `/api/admin/file-upload-policy` | Replace the effective upload policy. |
| `GET` | `/api/admin/file-uploads` | List automatic file-upload records. |

The two policy `GET` routes are aliases. They return the same singleton policy
and the same response shape.

## General conventions

- The API base URL depends on the deployment. Local server configuration uses
  `http://localhost:8080` unless the `PORT` environment variable overrides it.
- Request and response bodies use JSON with camel-case property names.
- Date/time values are UTC ISO 8601 strings, such as
  `2026-07-22T17:34:56.123456Z`.
- File sizes are integer byte counts, not MB or MiB.
- The server currently does **not** configure authentication, authorization, or
  admin-role checks for these routes. A production deployment must protect the
  admin routes at the application or reverse-proxy/API-gateway layer.
- Swagger UI is exposed at `/swagger`; the generated OpenAPI document is at
  `/swagger/v1/swagger.json`.

## File upload policy object

| Property | Type | Description |
|---|---|---|
| `isEnabled` | boolean | Whether agents may initiate automatic uploads. |
| `extensions` | string[] | Normalized, distinct file extensions that agents may upload. |
| `maxFileSizeBytes` | int64 | Maximum allowed size of one file, in bytes. |
| `updatedAtUtc` | string (date-time) | UTC time at which the policy was created or last updated. Response only. |

Extension values are normalized whenever the policy is updated or read:

1. Null, empty, and whitespace-only entries are removed.
2. Leading/trailing whitespace is removed.
3. All leading periods are removed and one period is added back.
4. Values are converted to lowercase.
5. Duplicates are removed case-insensitively.
6. The final list is sorted.

For example, `" PDF "`, `".pdf"`, and `"..PDF"` all become one `".pdf"`
entry. The API does not restrict values to a predefined extension registry, so
a custom value such as `".excle"` is accepted if it passes the general
validation rules.

If no policy exists yet, either policy `GET` creates and returns a default
policy with:

- `isEnabled`: `true`
- `maxFileSizeBytes`: `1073741824` (1 GiB)
- `extensions`: `.pdf`, `.doc`, `.docx`, `.docm`, `.dot`, `.dotx`, `.dotm`,
  `.rtf`, `.txt`, `.odt`, `.xls`, `.xlsx`, `.xlsm`, `.xlsb`, `.xlt`, `.xltx`,
  `.xltm`, `.xla`, `.xlam`, `.xlw`, `.csv`, `.xml`, `.dif`, `.slk`, `.ods`,
  `.ppt`, `.pptx`, `.pptm`, `.pps`, `.ppsx`, `.ppsm`, `.pot`, `.potx`,
  `.potm`, `.ppa`, `.ppam`, `.odp`, `.vsd`, `.vsdx`, `.vsdm`, `.vss`,
  `.vssx`, `.vst`, `.vstx`, `.vstm`, `.one`, `.onepkg`, `.pub`, `.mpp`,
  `.mpt`, `.mdb`, `.accdb`, `.accde`, `.accdt`, `.accdr`, `.pst`, `.ost`,
  `.msg`, `.eml`, `.xps`, and `.oxps`

## Read the agent upload policy

```http
GET /api/agent/file-uploads/policy
Accept: application/json
```

Agents should call this endpoint before scanning or initiating automatic file
uploads. A disabled policy prevents new upload initiation. The upload-initiate
endpoint also enforces the extension and maximum-size rules server-side.

### Successful response

`200 OK`

```json
{
  "isEnabled": true,
  "extensions": [
    ".csv",
    ".doc",
    ".docm",
    ".docx",
    ".excle",
    ".pdf",
    ".ppt",
    ".pptm",
    ".pptx",
    ".xls",
    ".xlsb",
    ".xlsm",
    ".xlsx"
  ],
  "maxFileSizeBytes": 1073741824,
  "updatedAtUtc": "2026-07-22T17:34:56.123456Z"
}
```

### Example

```bash
curl -sS \
  -H "Accept: application/json" \
  "https://api.example.com/api/agent/file-uploads/policy"
```

## Read the admin upload policy

```http
GET /api/admin/file-upload-policy
Accept: application/json
```

This is an admin-facing alias of the agent policy endpoint.

### Successful response

`200 OK` with the [file upload policy object](#file-upload-policy-object).

### Example

```bash
curl -sS \
  -H "Accept: application/json" \
  "https://api.example.com/api/admin/file-upload-policy"
```

## Replace the admin upload policy

```http
PUT /api/admin/file-upload-policy
Content-Type: application/json
Accept: application/json
```

The request replaces all configurable values in the singleton policy. It is
not a partial update.

### Request body

| Property | Type | Validation |
|---|---|---|
| `isEnabled` | boolean | Enables or disables new automatic uploads. |
| `maxFileSizeBytes` | int64 | Must be greater than `0`. |
| `extensions` | string[] | Must produce 1-500 distinct normalized values. Each normalized value must be at most 32 characters, including the leading period. |

All three properties should be supplied. Because the server binds this body to
a DTO with defaults, an omitted `isEnabled` becomes `true`, an omitted
`maxFileSizeBytes` becomes `1073741824`, and an omitted `extensions` fails the
non-empty-list validation.

### Request example

```json
{
  "isEnabled": true,
  "maxFileSizeBytes": 1073741824,
  "extensions": [
    ".pdf",
    ".doc",
    ".docx",
    ".docm",
    ".xls",
    ".xlsx",
    ".xlsm",
    ".xlsb",
    ".csv",
    ".ppt",
    ".pptx",
    ".pptm",
    ".excle"
  ]
}
```

### Successful response

`200 OK` returns the complete saved policy, including the server-generated
`updatedAtUtc` value. The extensions in the response may have a different order
from the request because the server sorts the normalized list.

```json
{
  "isEnabled": true,
  "extensions": [
    ".csv",
    ".doc",
    ".docm",
    ".docx",
    ".excle",
    ".pdf",
    ".ppt",
    ".pptm",
    ".pptx",
    ".xls",
    ".xlsb",
    ".xlsm",
    ".xlsx"
  ],
  "maxFileSizeBytes": 1073741824,
  "updatedAtUtc": "2026-07-22T17:34:56.123456Z"
}
```

### Validation errors

| Status | Condition | Response message |
|---|---|---|
| `400 Bad Request` | `extensions` is null or empty | `At least one file extension is required.` |
| `400 Bad Request` | Normalization removes every extension | `At least one valid file extension is required.` |
| `400 Bad Request` | More than 500 distinct normalized extensions | `A maximum of 500 file extensions is allowed.` |
| `400 Bad Request` | A normalized extension exceeds 32 characters | `A file extension cannot be longer than 32 characters.` |
| `400 Bad Request` | `maxFileSizeBytes` is zero or negative | `Maximum file size must be greater than zero.` |
| `400 Bad Request` | JSON cannot be bound to the request model | ASP.NET Core validation problem details. |
| `415 Unsupported Media Type` | A body is sent using an unsupported content type | ASP.NET Core error response. |

### Example

```bash
curl -sS -X PUT \
  -H "Content-Type: application/json" \
  -H "Accept: application/json" \
  --data '{
    "isEnabled": true,
    "maxFileSizeBytes": 1073741824,
    "extensions": [".pdf", ".docx", ".xlsx", ".csv"]
  }' \
  "https://api.example.com/api/admin/file-upload-policy"
```

### Update behavior

- The update is persisted before the response is returned.
- The policy is a singleton shared by every agent.
- There is no version, ETag, or optimistic-concurrency check; concurrent updates
  use last-write-wins behavior.
- Disabling the policy prevents the initiation of new uploads. This endpoint
  does not abort multipart uploads that are already in progress.

## List automatic file uploads

```http
GET /api/admin/file-uploads?deviceCode={deviceCode}&status={status}&take={take}
Accept: application/json
```

Returns the most recently updated automatic file-upload records. The response
is a JSON array; there is no wrapper object and no continuation token.

### Query parameters

| Parameter | Type | Required | Default | Behavior |
|---|---|---:|---:|---|
| `deviceCode` | string | No | none | Trimmed, converted to uppercase, then matched exactly. |
| `status` | string | No | none | Trimmed, converted to lowercase, then matched exactly. |
| `take` | int32 | No | `200` | Number of records requested. Values are clamped to `1`-`1000`. |

Examples of `take` clamping:

- `take=0` returns at most 1 record.
- `take=5000` returns at most 1000 records.
- A non-integer value cannot be model-bound and produces `400 Bad Request`.

Results are ordered by `updatedAtUtc` descending. There is currently no offset,
cursor, total count, or guaranteed secondary sort order for equal timestamps.

### Upload record

| Property | Type | Nullable | Description |
|---|---|---:|---|
| `id` | string (UUID) | No | Upload record identifier. |
| `deviceCode` | string | No | Normalized device code. |
| `fullPath` | string | No | Original source path reported by the agent. |
| `fileName` | string | No | Sanitized file name. |
| `extension` | string | No | Normalized lowercase extension with a leading period. |
| `sizeBytes` | int64 | No | Source file size in bytes. |
| `lastModifiedAtUtc` | string (date-time) | No | Source file's last-modified time in UTC. |
| `status` | string | No | Current upload state. |
| `errorMessage` | string | No | Most recently reported upload error, or an empty string. |
| `createdAtUtc` | string (date-time) | No | Time the database record was first created. |
| `updatedAtUtc` | string (date-time) | No | Time the record was most recently changed. |
| `completedAtUtc` | string (date-time) | Yes | Completion time; null until successfully completed. |

Known status values are:

| Status | Meaning |
|---|---|
| `uploading` | Multipart upload is active or resumable. |
| `completed` | Multipart upload completed successfully. |
| `aborted` | Multipart upload was explicitly aborted. |
| `failed` | Reserved by the data model for a failed upload. The current failure-report endpoint keeps resumable records in `uploading` rather than assigning this status. |

The `status` filter is not enum-validated by this endpoint. An unknown status
normally returns an empty array rather than a validation error.

### Successful response

`200 OK`

```json
[
  {
    "id": "b5cafb45-9503-4ae1-893d-32a93684c688",
    "deviceCode": "PC-001",
    "fullPath": "C:\\Users\\operator\\Documents\\report.pdf",
    "fileName": "report.pdf",
    "extension": ".pdf",
    "sizeBytes": 2483942,
    "lastModifiedAtUtc": "2026-07-22T13:45:12Z",
    "status": "completed",
    "errorMessage": "",
    "createdAtUtc": "2026-07-22T13:46:01.123456Z",
    "updatedAtUtc": "2026-07-22T13:46:08.654321Z",
    "completedAtUtc": "2026-07-22T13:46:08.650000Z"
  },
  {
    "id": "833f68db-7316-4bf3-a31d-d84819d644f9",
    "deviceCode": "PC-001",
    "fullPath": "C:\\Users\\operator\\Documents\\budget.xlsx",
    "fileName": "budget.xlsx",
    "extension": ".xlsx",
    "sizeBytes": 912340,
    "lastModifiedAtUtc": "2026-07-22T12:02:30Z",
    "status": "uploading",
    "errorMessage": "Network connection interrupted.",
    "createdAtUtc": "2026-07-22T12:03:00.100000Z",
    "updatedAtUtc": "2026-07-22T12:04:42.200000Z",
    "completedAtUtc": null
  }
]
```

If no records match, the endpoint returns `200 OK` with an empty array:

```json
[]
```

### Examples

List the latest uploads using the default limit:

```bash
curl -sS \
  -H "Accept: application/json" \
  "https://api.example.com/api/admin/file-uploads"
```

List up to 100 completed uploads for one device:

```bash
curl -sS \
  -H "Accept: application/json" \
  "https://api.example.com/api/admin/file-uploads?deviceCode=PC-001&status=completed&take=100"
```

## Common operational errors

The endpoints can return `500 Internal Server Error` when the PostgreSQL
database is unavailable or persistence fails. The policy reads and upload-list
endpoint do not access object storage. Error payloads for unhandled failures
follow the ASP.NET Core environment configuration and should not be treated as
a stable client contract.

## Security and privacy notes

- `PUT /api/admin/file-upload-policy` changes upload behavior for every agent.
- `GET /api/admin/file-uploads` exposes device codes, full local file paths,
  file names, sizes, timestamps, statuses, and error text.
- Restrict both admin routes to authorized administrators before exposing this
  service outside a trusted network.
- Use HTTPS at the deployment edge so policy and file metadata are encrypted in
  transit.
- Consider logging policy changes and access to the administrative upload list,
  because the current controller does not create an audit trail.
