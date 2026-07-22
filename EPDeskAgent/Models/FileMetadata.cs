namespace EPDeskAgent.Models
{
    public class FileMetadata
    {
        public long Id { get; set; }
        public string DeviceCode { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Extension { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }
        public bool IsDeleted { get; set; }
        public string SyncStatus { get; set; } = "pending";
        public long? UploadedSizeBytes { get; set; }
        public DateTime? UploadedUpdatedAtUtc { get; set; }
        public string UploadStatus { get; set; } = "pending";
        public string UploadError { get; set; } = "";
        public DateTime? LastUploadAttemptAtUtc { get; set; }
    }
}
