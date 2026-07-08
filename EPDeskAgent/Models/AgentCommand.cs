namespace EPDeskAgent.Models;

public class AgentCommand
{
    public string Type { get; set; } = "";

    public string RequestType { get; set; } = "";

    public Guid RequestId { get; set; }

    public string FilePath { get; set; } = "";

    public string FolderPath { get; set; } = "";

    public List<string> Paths { get; set; } = new();
}