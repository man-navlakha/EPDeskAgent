namespace EPDeskExtractionWorker.Services.Jobs;

public static class ExtractionJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string RetryWait = "retry_wait";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string DeadLetter = "dead_letter";
}

public sealed record ClaimExtractionJobRequest
{
    public required string LeaseOwner { get; init; }

    public required string PipelineVersion { get; init; }

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public Guid? AllowedJobId { get; init; }

    public bool RequireTrustedSourceIdentity { get; init; } = true;

    public IReadOnlyList<string> AllowedExtensions { get; init; } =
        Array.Empty<string>();
}

public readonly record struct ExtractionJobLease(
    Guid JobId,
    string LeaseToken
);

/// <summary>
/// One claimed job plus the immutable source snapshot and document metadata
/// needed by the downloader and extractors. The lease token must accompany
/// every state-changing call made for this job.
/// </summary>
public sealed record ClaimedExtractionJob
{
    public required Guid JobId { get; init; }

    public required Guid DocumentVersionId { get; init; }

    public required string PipelineVersion { get; init; }

    public required int Priority { get; init; }

    public required int AttemptCount { get; init; }

    public required int MaxAttempts { get; init; }

    public required string LeaseOwner { get; init; }

    public required string LeaseToken { get; init; }

    public required DateTime LeaseUntilUtc { get; init; }

    public required Guid DocumentId { get; init; }

    public required int VersionNumber { get; init; }

    public required string SourceVersionKey { get; init; }

    public required string FileName { get; init; }

    public required string FileExtension { get; init; }

    public required string BucketName { get; init; }

    public required string ObjectKey { get; init; }

    public required string B2VersionId { get; init; }

    public required string ObjectETag { get; init; }

    public required long SizeBytes { get; init; }

    public DateTime? SourceModifiedAtUtc { get; init; }

    public required string DeclaredContentType { get; init; }

    /// <summary>
    /// Previously recorded source digest, when available. An empty value means
    /// the downloader must establish it during this extraction attempt.
    /// </summary>
    public required string Sha256 { get; init; }

    public required string DerivativePrefix { get; init; }

    public required string SourceType { get; init; }

    public required Guid SourceRecordId { get; init; }

    public required string DeviceCode { get; init; }

    public required string DisplayName { get; init; }

    public required string Classification { get; init; }

    public required string Department { get; init; }

    public required bool IsDeleted { get; init; }

    public ExtractionJobLease Lease => new(JobId, LeaseToken);
}

public sealed record ExtractedDocumentSection
{
    public required int Ordinal { get; init; }

    public required string SectionType { get; init; }

    public int? SectionNumber { get; init; }

    public string Heading { get; init; } = "";

    public string Content { get; init; } = "";

    public string OcrContent { get; init; } = "";

    public required string ContentHash { get; init; }

    public int CharacterCount { get; init; }

    public int? TokenCount { get; init; }

    public string Language { get; init; } = "";

    public string LocatorJson { get; init; } = "{}";

    public string MetadataJson { get; init; } = "{}";
}

public sealed record StoredDocumentDerivative
{
    public required string Kind { get; init; }

    public required int Ordinal { get; init; }

    public required string BucketName { get; init; }

    public required string ObjectKey { get; init; }

    public string B2VersionId { get; init; } = "";

    public string ObjectETag { get; init; } = "";

    public string ContentType { get; init; } = "application/octet-stream";

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public string MetadataJson { get; init; } = "{}";
}

public sealed record SuccessfulExtraction
{
    public required string DetectedContentType { get; init; }

    public required string SourceB2VersionId { get; init; }

    public required string SourceObjectETag { get; init; }

    public required string Sha256 { get; init; }

    public int? PageCount { get; init; }

    public string MetadataJson { get; init; } = "{}";

    public IReadOnlyList<ExtractedDocumentSection> Sections { get; init; } =
        Array.Empty<ExtractedDocumentSection>();

    public IReadOnlyList<StoredDocumentDerivative> Derivatives { get; init; } =
        Array.Empty<StoredDocumentDerivative>();
}

public enum ExtractionFailureDisposition
{
    Retry,
    Release,
    Reject,
    DeadLetter
}

public sealed record ExtractionFailure
{
    public required string ErrorCode { get; init; }

    public required string ErrorMessage { get; init; }

    public ExtractionFailureDisposition Disposition { get; init; } =
        ExtractionFailureDisposition.Retry;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record ExtractionFailureTransition
{
    public required string Status { get; init; }

    public required int AttemptCount { get; init; }

    public required int MaxAttempts { get; init; }

    public DateTime? NextAttemptAtUtc { get; init; }

    public bool IsTerminal =>
        Status is ExtractionJobStatuses.Rejected or
            ExtractionJobStatuses.DeadLetter;
}
