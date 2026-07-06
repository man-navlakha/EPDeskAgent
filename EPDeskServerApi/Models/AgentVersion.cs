namespace EPDeskServerApi.Models;

public class AgentVersion
{
    public Guid Id { get; set; }

    public string Version { get; set; } = "";

    public string DownloadUrl { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public bool IsMandatory { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public string ReleaseNotes { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}