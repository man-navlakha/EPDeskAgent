using NpgsqlTypes;

namespace EPDeskServerApi.Models;

/// <summary>
/// Searchable unit of extracted content, such as a page, slide, heading, or
/// spreadsheet table. Keeping sections separate preserves precise citations.
/// </summary>
public sealed class DocumentSection
{
    public Guid Id { get; set; }

    public Guid DocumentVersionId { get; set; }

    public DocumentVersion DocumentVersion { get; set; } = null!;

    public string PipelineVersion { get; set; } = "v1";

    public int Ordinal { get; set; }

    // page, slide, heading, table, sheet
    public string SectionType { get; set; } = "";

    public int? SectionNumber { get; set; }

    public string Heading { get; set; } = "";

    public string Content { get; set; } = "";

    public string OcrContent { get; set; } = "";

    public string ContentHash { get; set; } = "";

    public int CharacterCount { get; set; }

    public int? TokenCount { get; set; }

    public string Language { get; set; } = "";

    public string LocatorJson { get; set; } = "{}";

    public string MetadataJson { get; set; } = "{}";

    /// <summary>
    /// PostgreSQL-generated search vector built from heading, content, and OCR
    /// content. The value is populated by PostgreSQL rather than application code.
    /// </summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
