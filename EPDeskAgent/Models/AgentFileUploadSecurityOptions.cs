namespace EPDeskAgent.Models;

public sealed class AgentFileUploadSecurityOptions
{
    public const string SectionName = "Security";
    public const string HeaderName = "X-Agent-File-Upload-Key";
    public const int MinimumApiKeyLength = 32;

    public string AgentFileUploadApiKey { get; set; } = "";

    public static bool HasValidApiKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) &&
        apiKey.Length >= MinimumApiKeyLength &&
        string.Equals(apiKey, apiKey.Trim(), StringComparison.Ordinal) &&
        apiKey.All(character => !char.IsControl(character));
}
