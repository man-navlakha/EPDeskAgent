namespace EPDeskServerApi.Models;

/// <summary>
/// Immutable snapshot of one document object in Backblaze B2. Large extracted
/// content is stored in DocumentSection rows, not on this metadata record.
/// </summary>
public sealed class DocumentVersion
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public Document Document { get; set; } = null!;

    public int VersionNumber { get; set; } = 1;

    /// <summary>
    /// Deterministic key for one source revision. This makes job creation and
    /// backfills idempotent even when the source upload row is mutable.
    /// </summary>
    public string SourceVersionKey { get; set; } = "";

    public string FileName { get; set; } = "";

    public string FileExtension { get; set; } = "";

    public string BucketName { get; set; } = "";

    public string ObjectKey { get; set; } = "";

    public string B2VersionId { get; set; } = "";

    public string ObjectETag { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime? SourceModifiedAtUtc { get; set; }

    public string DeclaredContentType { get; set; } = "application/octet-stream";

    public string DetectedContentType { get; set; } = "";

    public string Sha256 { get; set; } = "";

    // pending, queued, processing, completed, rejected, failed
    public string ExtractionStatus { get; set; } = "pending";

    public int? PageCount { get; set; }

    public int SectionCount { get; set; }

    public string ExtractionPipelineVersion { get; set; } = "";

    /// <summary>
    /// Small structured summary such as sheet names and page counts. Full text,
    /// tables, and other large output belong in sections or B2 derivatives.
    /// </summary>
    public string ExtractionMetadataJson { get; set; } = "{}";

    public string DerivativePrefix { get; set; } = "";

    public string ExtractionErrorCode { get; set; } = "";

    public string ExtractionError { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ExtractedAtUtc { get; set; }

    public List<ExtractionJob> ExtractionJobs { get; set; } = [];

    public List<DocumentSection> Sections { get; set; } = [];

    public List<DocumentDerivative> Derivatives { get; set; } = [];
}
