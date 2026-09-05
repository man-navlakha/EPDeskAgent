namespace EPDeskServerApi.Models;

public sealed class OldUserDataFile
{
    public Guid Id { get; set; }

    public Guid ImportJobId { get; set; }

    public OldUserDataImportJob ImportJob { get; set; } = null!;

    public string UserFolder { get; set; } = "";

    public string DeviceCode { get; set; } = "";

    public string FullPath { get; set; } = "";

    public string RelativePath { get; set; } = "";

    public string FileName { get; set; } = "";

    public string Extension { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public string ObjectKey { get; set; } = "";

    public string B2VersionId { get; set; } = "";

    public string ObjectETag { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string MultipartUploadId { get; set; } = "";

    public long PartSizeBytes { get; set; }

    public long UploadedBytes { get; set; }

    // indexed, uploading, completed, failed, skipped, missing
    public string Status { get; set; } = "indexed";

    public int AttemptCount { get; set; }

    public string ErrorMessage { get; set; } = "";

    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UploadStartedAtUtc { get; set; }

    /// <summary>
    /// Held by whichever worker is currently uploading this file. A row whose lease
    /// has expired is treated as abandoned and can be claimed again, which is how a
    /// killed process releases its work without leaving files stuck forever.
    /// </summary>
    public DateTime? LeaseUntilUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
