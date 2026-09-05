namespace EPDeskExtractionWorker.Configuration;

public sealed class B2StorageOptions
{
    public const string SectionName = "B2";

    public string ServiceUrl { get; set; } = "";

    public string Region { get; set; } = "";

    public string BucketName { get; set; } = "";

    public string KeyId { get; set; } = "";

    public string ApplicationKey { get; set; } = "";

    public string ConnectionTestPrefix { get; set; } = "";
}
