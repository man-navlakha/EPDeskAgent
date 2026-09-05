namespace EPDeskServerApi.Models;

public sealed class OldUserDataImportJob
{
    public Guid Id { get; set; }

    public string RootPath { get; set; } = "";

    public string RootPathIdentity { get; set; } = "";

    public string SourceLabel { get; set; } = "Old User Data";

    // pending, scanning, uploading, completed, completed_with_errors, failed, paused
    public string Status { get; set; } = "pending";

    public long IndexedFileCount { get; set; }

    public long IndexedSizeBytes { get; set; }

    public long UploadedFileCount { get; set; }

    public long UploadedSizeBytes { get; set; }

    public long FailedFileCount { get; set; }

    public string ErrorMessage { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ScanCompletedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public List<OldUserDataFile> Files { get; set; } = [];
}
