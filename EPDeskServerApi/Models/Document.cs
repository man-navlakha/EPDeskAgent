namespace EPDeskServerApi.Models;

/// <summary>
/// Canonical file identity shared by every upload source. A document can have
/// multiple immutable versions as the source file changes over time.
/// </summary>
public sealed class Document
{
    public Guid Id { get; set; }

    // automatic_upload, old_user_data
    public string SourceType { get; set; } = "";

    /// <summary>
    /// ID of the AutomaticFileUpload, OldUserDataFile, or another future
    /// ingestion record that owns this document.
    /// </summary>
    public Guid SourceRecordId { get; set; }

    public string DeviceCode { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Classification { get; set; } = "unclassified";

    public string Department { get; set; } = "";

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<DocumentVersion> Versions { get; set; } = [];
}
