---
name: epdesk-files
description: Locate and download company files collected by EPDesk from Windows laptops and the old-device archive, via the epdesk MCP server. Use when asked to find a file by name or Windows path, list what a device holds, check whether a file was ever uploaded, produce a download link, read a small text file, or report on storage volume and the device fleet. This server exposes file metadata and downloads only — it cannot search or read what is inside a document. Triggers on "find the file", "locate", "download link for", "did this file get uploaded", "what files do we have", "how much data have we collected", "which device has", "old device data", "epdesk".
---

# EPDesk file access

EPDesk collects files from company Windows machines and stores them in private
object storage. Two ingestion sources feed one catalogue:

- `automatic_upload` — the agent running on staff laptops, still growing
- `old_user_data` — a bulk import of older device archives

Both are reachable through the same tools, and `sourceType` is the filter that
separates them.

## What this server does and does not do

It answers **what files exist, where they came from, and give me that file.**
It does **not** look inside files. There is no full-text search and no way to
read a PDF's text through this connector — that pipeline is shelved (see
[Extraction](#extraction-is-shelved)).

Eight tools are exposed. If you see fewer, or the `epdesk_*` tools are missing
entirely, the connector did not load — say so rather than guessing at answers.

Registered for this repository in `.mcp.json`, served from Railway (project
`laptop-data-api`, service `epdesk-mcp`):

```
https://epdesk-mcp-production.up.railway.app/mcp
```

Stateless streamable HTTP, unauthenticated, no credentials to configure. Setup
and redeployment live in `EPDeskMcpServer/README.md`.

## The three ids

Every tool speaks in these, and they chain:

- `documentId` — one logical file, stable as it is revised
- `versionId` — one immutable revision of that file
- `derivativeId` — one artefact stored against a version

List returns all the ids you need. Never construct one.

## Response shapes

Every list tool wraps its rows in `items` (not `documents` / `devices`), beside
`totalCount`, `offset`, `limit`, `returned`, and `nextOffset`. Size fields are
named per tool and are easy to guess wrong: `totalSizeBytes` on storage stats,
`documentSizeBytes` on a device row, `sizeBytes` on a file. The presigned link
from `epdesk_get_download_url` is `downloadUrl`, with `expiresAtUtc` beside it.

## Pick the right entry point

| The question | Start with |
| --- | --- |
| "Find the file called X" / "the one under C:\Users\..." | `epdesk_list_documents` |
| "What has this device got?" | `epdesk_list_documents` with `deviceCode` |
| "Tell me about this file" | `epdesk_get_document` |
| "Is it intact / where is it stored?" | `epdesk_get_file_metadata` |
| "Send me the file" | `epdesk_get_download_url` |
| "Read me this CSV / JSON / log" | `epdesk_read_file_content` |
| "How much have we collected?" | `epdesk_get_storage_stats` |
| "Which machines report in?" | `epdesk_list_devices` |
| "Did this file ever arrive?" | `epdesk_list_uploads` |

## Finding a file without search

`epdesk_list_documents` is the only way in, and it matches on metadata only:

- `nameContains` — substring of the filename
- `pathContains` — substring of the original Windows path, which is often the
  best signal you have. `C:\Users\Radhika\Downloads` narrows to one person's
  downloads instantly.
- `deviceCode`, `department`, `classification`, `sourceType`, `extension`
- `minSizeBytes` / `maxSizeBytes`, `modifiedAfterUtc` / `modifiedBeforeUtc`
- `sortBy` — `updated` (default), `created`, `name`, or `size`, with
  `descending`

Omitting `limit` gives 20 rows and the server caps it at 100, so narrow with a
filter rather than paging through the whole catalogue.

**When the user describes contents rather than a filename, say so plainly.**
"Which file mentions the Godrej hoarding rates" cannot be answered here. Do not
substitute a `nameContains` guess and present the result as if it were a
content match — filenames in this corpus look like
`13377-so-salestaxinvoice-20260102180519.pdf`, so guessing is close to
worthless. Offer what does work: filter by device, path, extension, or date
range, then hand over candidates for the user to open.

An empty result means no file matched **those filters**. Widen before
concluding anything, and use `epdesk_list_uploads` to check whether the file
ever arrived at all — a row there with no `documentId` never made it into the
catalogue.

## Getting files out

This is the point of the server. Every stored file can be downloaded, whatever
its type.

- `epdesk_get_download_url` returns a private, time-limited link for a
  document, a specific version, or a derivative. Pass a short
  `expiresInMinutes` when sharing, and state the expiry when you hand it over.
  The link is signed for `GET` only — a `HEAD` against it returns 403, which is
  expected and not a fault.
- `epdesk_read_file_content` returns raw bytes inline for text-like files — a
  CSV to parse, a JSON config, a log. Large binaries are refused with a pointer
  to the download URL. PDFs and Office files are not readable as text here;
  hand over a download link instead.

**The download URL is a bearer credential for that one file.** Anyone holding
it gets the file with no login. Do not paste it anywhere the user did not ask
for it, and do not include it in a summary that will travel further than the
request.

## Reporting on the corpus

`epdesk_get_storage_stats` accepts the same filters as `epdesk_list_documents`,
so scope it before summarising. Read its fields precisely:

- `documentCount` counts logical files; `versionCount` counts stored revisions.
- `totalSizeBytes` is the corpus size in bytes — convert it before quoting.
- `fileCount` inside a breakdown row (`byExtension`, `byDevice`, `byDepartment`,
  `bySourceType`) counts **versions**, not documents.

`bySourceType` is how you split laptop uploads from the old-device import.
`epdesk_list_devices` gives the fleet with `documentCount` and
`documentSizeBytes` per machine, and is where you learn valid `deviceCode`
values before filtering anything else by device.

## Handling the data

This is private company material: invoices, purchase orders, client proposals,
and personal documents from named employees' machines. Return what was asked
for. Do not go fishing through unrelated people's files to pad an answer, and do
not surface personal documents that have nothing to do with the question.

## Extraction is shelved

The text-extraction pipeline — full-text search, extracted-text reads, queue
health, and requeue — was deliberately removed from this server's surface. The
code is intact in `EPDeskMcpServer/Tools/ExtractionTools.cs`; it is simply not
registered in `Program.cs`, which registers tool types explicitly instead of
scanning the assembly.

If asked why search is gone, say it is turned off for now and expected back —
not that it failed or that the data is missing. Nothing was deleted, and every
file is still downloadable.

Restoring it takes three things: add `.WithTools<ExtractionTools>()` to the
builder chain in `Program.cs`, restore the extraction paragraphs in
`ServerInstructions`, and start the `epdesk-extraction-worker` Railway service
so the queue drains. `epdesk_requeue_extraction` additionally needs
`Mcp__EnableWriteTools=true`.
