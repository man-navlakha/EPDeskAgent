# Document extraction schema

The extraction schema separates small searchable database records from large
binary or structured derivatives.

## Data ownership

- `Documents` identifies one logical source file. `SourceType` and
  `SourceRecordId` point to an `AutomaticFileUpload`, `OldUserDataFile`, or a
  future ingestion source without adding a polymorphic foreign key.
- `DocumentVersions` stores an immutable snapshot of the B2 object and the
  extraction summary. `SourceVersionKey` makes enqueue and backfill operations
  idempotent when an upload source row is reused.
- `ExtractionJobs` is the durable PostgreSQL work queue. A worker must use an
  atomic lease claim and must guard heartbeat/completion updates with the
  current `LeaseToken`.
- `DocumentSections` stores the extracted text in page, slide, heading, table,
  or sheet-sized records. PostgreSQL maintains `SearchVector` and its GIN index.
- `DocumentDerivatives` points to generated B2 objects such as extraction JSON,
  OCR PDFs, thumbnails, or Parquet tables.

Full document text and binary data must not be stored in `Documents` or
`DocumentVersions`. `ExtractionMetadataJson` is for a small summary such as
page count, workbook sheet names, and table counts.

## Pipeline visibility

Sections and derivatives are keyed by `PipelineVersion`. During reprocessing,
the worker can write a new pipeline version without exposing partial results.
After all output is durable, one transaction updates the `DocumentVersion`:

```text
ExtractionStatus = completed
ExtractionPipelineVersion = <new pipeline version>
SectionCount = <committed section count>
ExtractedAtUtc = now
```

Search queries must only use sections whose `PipelineVersion` matches the
version's active `ExtractionPipelineVersion` and whose document is not deleted.

## Deployment order

1. Apply the API-owned EF migration.
2. Deploy API enqueue logic for completed uploads.
3. Backfill completed existing upload rows into documents, versions, and jobs.
4. Deploy worker claim/download/verification logic with processing disabled.
5. Enable processing after sample jobs pass.

See `DOCUMENT_EXTRACTION_QUEUE_API.md` for the authenticated backfill request
and staged rollout procedure.

The extraction worker must never run EF migrations. Database migrations remain
owned by `EPDeskServerApi`.
