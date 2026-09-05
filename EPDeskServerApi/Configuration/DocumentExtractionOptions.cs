namespace EPDeskServerApi.Configuration;

public sealed class DocumentExtractionOptions
{
    public const string SectionName = "DocumentExtraction";

    /// <summary>
    /// Dedicated key for document-extraction administration endpoints. Configure
    /// it only through secrets, for example DocumentExtraction__AdminApiKey.
    /// </summary>
    public string AdminApiKey { get; set; } = "";
}
