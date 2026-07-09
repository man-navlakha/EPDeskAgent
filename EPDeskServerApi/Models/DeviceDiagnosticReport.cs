namespace EPDeskServerApi.Models;

public class DeviceDiagnosticReport
{
    public Guid Id { get; set; }

    public string DeviceCode { get; set; } = "";

    public Guid? CommandId { get; set; }

    public string AgentVersion { get; set; } = "";

    public string ServiceStatus { get; set; } = "";

    public string WindowsVersion { get; set; } = "";

    public string ServiceAccount { get; set; } = "";

    public bool InternetWorking { get; set; }

    public bool ApiReachable { get; set; }

    public long SystemDriveFreeBytes { get; set; }

    public string CurrentRunningTask { get; set; } = "";

    public string LastFileRequest { get; set; } = "";

    public string LastError { get; set; } = "";

    public DateTime? LastHeartbeatUtc { get; set; }

    public int PendingRequestsCount { get; set; }

    public string DetailsJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}