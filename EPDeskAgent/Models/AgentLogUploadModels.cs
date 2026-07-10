namespace EPDeskAgent.Models;

public class UploadAgentLogsRequest
{
    public string DeviceCode { get; set; } = "";

    public Guid? CommandId { get; set; }

    public List<AgentLogUploadItem> Logs { get; set; } = new();
}

public class AgentLogUploadItem
{
    public Guid? RequestId { get; set; }

    public string Level { get; set; } = "INFO";

    public string Category { get; set; } = "";

    public string Step { get; set; } = "";

    public string Message { get; set; } = "";

    public string DetailsJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class RequestLogsPayload
{
    public string LogType { get; set; } = "all";

    public int TakeLines { get; set; } = 500;
}