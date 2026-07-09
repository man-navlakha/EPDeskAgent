namespace EPDeskServerApi.Models;

public class RemoteCommand
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public string CommandType { get; set; } = "";
    // REQUEST_LOGS, RUN_DIAGNOSTICS

    public string PayloadJson { get; set; } = "";

    public string Status { get; set; } = "pending";
    // pending, sent_to_agent, completed, failed, expired

    public string RequestedBy { get; set; } = "";

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? SentAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string ErrorMessage { get; set; } = "";
}