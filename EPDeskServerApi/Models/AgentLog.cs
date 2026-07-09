namespace EPDeskServerApi.Models;

public class AgentLog
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public Guid? RequestId { get; set; }

    public string Level { get; set; } = "INFO";
    // INFO, WARNING, ERROR

    public string Category { get; set; } = "";
    // agent, file-request, upload, error, diagnostic

    public string Message { get; set; } = "";

    public string Step { get; set; } = "";
    // request_received, path_checked, zipping_started, upload_started, completed, failed

    public string DetailsJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}