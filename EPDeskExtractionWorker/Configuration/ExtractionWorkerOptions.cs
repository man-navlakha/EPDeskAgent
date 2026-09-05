namespace EPDeskExtractionWorker.Configuration;

public sealed class ExtractionWorkerOptions
{
    public const string SectionName = "ExtractionWorker";

    /// <summary>
    /// Master safety switch. Keep false until the API migration and queue
    /// backfill have been verified.
    /// </summary>
    public bool ProcessingEnabled { get; set; }

    public string PipelineVersion { get; set; } = "v1";

    public int PollIntervalSeconds { get; set; } = 5;

    public int MaxConcurrentJobs { get; set; } = 1;

    public int LeaseSeconds { get; set; } = 300;

    public int HeartbeatSeconds { get; set; } = 60;

    public int ProcessingTimeoutSeconds { get; set; } = 600;

    public int RetryBaseSeconds { get; set; } = 30;

    public long MaxDownloadBytes { get; set; } = 268_435_456;

    /// <summary>
    /// Zero means unlimited. Use one for the first production canary.
    /// </summary>
    public int MaxJobsPerInstanceLifetime { get; set; } = 1;

    public string CanaryJobId { get; set; } = "";

    public bool RequireTrustedSourceIdentity { get; set; } = true;

    public string[] AllowedExtensions { get; set; } =
        [".pdf", ".docx", ".pptx", ".txt", ".md"];

    public string SandboxBaseUrl { get; set; } = "";

    public string SandboxApiKey { get; set; } = "";

    public long MaxSandboxResponseBytes { get; set; } = 134_217_728;

    public string TempRoot { get; set; } = "/tmp/epdesk-extraction";

    public bool OcrImagesEnabled { get; set; }

    public bool IncludeSpeakerNotes { get; set; }
}
