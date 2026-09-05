namespace EPDeskServerApi.Configuration;

public sealed class OldUserDataImportOptions
{
    public const string SectionName = "OldUserDataImport";

    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = "";

    public string RootPath { get; set; } = "";

    public string SourceLabel { get; set; } = "Old User Data";

    public string ObjectKeyPrefix { get; set; } = "uploads/old-user-data";

    public int ScanBatchSize { get; set; } = 500;

    public int PollIntervalSeconds { get; set; } = 5;

    public int UploadRetryCount { get; set; } = 3;

    /// <summary>
    /// How many files are uploaded at the same time. Uploads are latency bound
    /// rather than bandwidth bound - every file costs roughly a dozen database
    /// round trips plus several Backblaze round trips - so raising this multiplies
    /// throughput until the connection itself saturates.
    /// </summary>
    public int UploadConcurrency { get; set; } = 4;

    /// <summary>
    /// How many parts of a single large file are uploaded at the same time.
    /// A single TCP stream to Backblaze tops out well below the available
    /// bandwidth, so large files need this to go fast.
    /// </summary>
    public int PartConcurrency { get; set; } = 4;

    /// <summary>
    /// How long a claimed file stays reserved. It is renewed as parts complete, so
    /// this only needs to outlast a single part upload, not the whole file.
    /// </summary>
    public int LeaseMinutes { get; set; } = 30;
}
