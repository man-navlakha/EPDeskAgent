namespace EPDeskAgent.Models;

public class AgentUpdateInfo
{
    public bool UpdateAvailable { get; set; }

    public string CurrentVersion { get; set; } = "";

    public string LatestVersion { get; set; } = "";

    public string DownloadUrl { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public bool IsMandatory { get; set; }

    public string ReleaseNotes { get; set; } = "";
}