namespace EPDeskServerApi.Models;

public class FileRequest
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public string RequestedPath { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string Reason { get; set; } = "";

    public string Status { get; set; } = "pending";
    // pending, sent_to_agent, uploading, completed, failed

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? ServerFilePath { get; set; }

    public string? OriginalFileName { get; set; }

    public string? ErrorMessage { get; set; }
}