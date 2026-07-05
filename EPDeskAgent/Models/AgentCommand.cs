namespace EPDeskAgent.Models;

public class AgentCommand
{
    public string Type { get; set; } = "";
    public Guid RequestId { get; set; }
    public string FilePath { get; set; } = "";
}