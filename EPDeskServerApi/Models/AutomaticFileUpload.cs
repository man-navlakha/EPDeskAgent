namespace EPDeskServerApi.Models;

public class AutomaticFileUpload
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public string FullPath { get; set; } = "";

    public string PathIdentity { get; set; } = "";

    public string FileName { get; set; } = "";

    public string Extension { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime LastModifiedAtUtc { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public string ObjectKey { get; set; } = "";

    public string MultipartUploadId { get; set; } = "";

    public long PartSizeBytes { get; set; }

    // uploading, completed, aborted, failed
    public string Status { get; set; } = "uploading";

    public string ErrorMessage { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }
}
