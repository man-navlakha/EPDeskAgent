namespace EPDeskServerApi.Models;

public class Device
{
    public Guid Id { get; set; }
    public string DeviceCode { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Status { get; set; } = "offline";
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
