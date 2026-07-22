namespace EPDeskAgent.Models;

public sealed class AutomaticFileUploadPolicyResponse
{
    public bool IsEnabled { get; set; }
    public List<string> Extensions { get; set; } = [];
    public long MaxFileSizeBytes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InitiateAutomaticFileUploadRequest
{
    public string DeviceCode { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModifiedAtUtc { get; set; }
}

public sealed class InitiateAutomaticFileUploadResponse
{
    public bool ShouldUpload { get; set; }
    public Guid UploadId { get; set; }
    public string Status { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public long PartSizeBytes { get; set; }
    public int ExpectedPartCount { get; set; }
    public List<int> UploadedPartNumbers { get; set; } = [];
}

public sealed class AutomaticFileUploadPartUrlResponse
{
    public Guid UploadId { get; set; }
    public int PartNumber { get; set; }
    public string UploadUrl { get; set; } = "";
    public long OffsetBytes { get; set; }
    public long LengthBytes { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
