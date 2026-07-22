namespace EPDeskServerApi.Models;

public class FileUploadPolicy
{
    public Guid Id { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string ExtensionsJson { get; set; } = "[]";

    public long MaxFileSizeBytes { get; set; } = 1024L * 1024 * 1024;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
