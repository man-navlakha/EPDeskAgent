namespace EPDeskServerApi.Models;

public class FileIndex
{
    public Guid Id { get; set; }
    public string DeviceCode { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime LastIndexedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}