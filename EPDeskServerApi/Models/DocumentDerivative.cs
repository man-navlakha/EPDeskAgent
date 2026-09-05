namespace EPDeskServerApi.Models;

/// <summary>
/// Metadata for a generated binary or structured object stored in B2, such as
/// extraction JSON, an OCR PDF, a thumbnail, or a Parquet table.
/// </summary>
public sealed class DocumentDerivative
{
    public Guid Id { get; set; }

    public Guid DocumentVersionId { get; set; }

    public DocumentVersion DocumentVersion { get; set; } = null!;

    public string PipelineVersion { get; set; } = "v1";

    // structured_json, ocr_pdf, thumbnail, parquet
    public string Kind { get; set; } = "";

    /// <summary>
    /// Allows multiple derivatives of the same kind, for example one Parquet
    /// object per workbook table.
    /// </summary>
    public int Ordinal { get; set; }

    public string BucketName { get; set; } = "";

    public string ObjectKey { get; set; } = "";

    public string B2VersionId { get; set; } = "";

    public string ObjectETag { get; set; } = "";

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    public string MetadataJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
