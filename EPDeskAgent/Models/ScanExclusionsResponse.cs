namespace EPDeskAgent.Models;

public class ScanExclusionsResponse
{
    public List<string> ExcludedFolders { get; set; } = new();

    public List<string> ExcludedFolderNames { get; set; } = new();

    public List<string> ExcludedFileExtensions { get; set; } = new();
}
