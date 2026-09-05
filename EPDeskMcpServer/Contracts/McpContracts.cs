using System.Text.Json.Serialization;

namespace EPDeskMcpServer.Contracts;

/// <summary>
/// Uniform envelope for every list-style tool. Carrying the total and the next
/// offset in-band means a model can decide whether to keep paging without
/// guessing from the size of the page it just received.
/// </summary>
public sealed record PagedResult<T>(
    int TotalCount,
    int Offset,
    int Limit,
    int Returned,
    int? NextOffset,
    IReadOnlyList<T> Items
);

public sealed record DocumentSummary(
    Guid DocumentId,
    Guid LatestVersionId,
    string FileName,
    string Extension,
    long SizeBytes,
    string DeviceCode,
    string Department,
    string Classification,
    string SourceType,
    string SourcePath,
    int VersionCount,
    int LatestVersionNumber,
    string ExtractionStatus,
    int SectionCount,
    int? PageCount,
    DateTime? SourceModifiedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record DocumentVersionSummary(
    Guid VersionId,
    int VersionNumber,
    string FileName,
    string FileExtension,
    long SizeBytes,
    string ExtractionStatus,
    int SectionCount,
    int? PageCount,
    string ExtractionErrorCode,
    string ExtractionError,
    DateTime? SourceModifiedAtUtc,
    DateTime? ExtractedAtUtc,
    IReadOnlyList<string> DerivativeKinds
);

public sealed record DocumentDetail(
    Guid DocumentId,
    string DisplayName,
    string SourceType,
    Guid SourceRecordId,
    string SourcePath,
    string DeviceCode,
    string Department,
    string Classification,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<DocumentVersionSummary> Versions
);

public sealed record SearchHit(
    Guid DocumentId,
    Guid VersionId,
    string FileName,
    string Extension,
    string DeviceCode,
    string Department,
    string Classification,
    string SourceType,
    string SourcePath,
    string SectionType,
    int? SectionNumber,
    string Heading,
    string Snippet,
    double Score,
    int MatchingSectionCount,
    DateTime? SourceModifiedAtUtc
);

public sealed record SearchResult(
    string Query,
    int TotalMatches,
    int Offset,
    int Limit,
    int Returned,
    int? NextOffset,
    bool GroupedByDocument,
    IReadOnlyList<SearchHit> Hits
);

public sealed record DocumentSectionContent(
    int Ordinal,
    string SectionType,
    int? SectionNumber,
    string Heading,
    string Content,
    int CharacterCount
);

public sealed record DocumentContent(
    Guid DocumentId,
    Guid VersionId,
    string FileName,
    string SourcePath,
    string ExtractionStatus,
    int TotalSections,
    int Offset,
    int Returned,
    int? NextOffset,
    bool Truncated,
    int CharactersReturned,
    IReadOnlyList<DocumentSectionContent> Sections
);

public sealed record DerivativeSummary(
    Guid DerivativeId,
    string Kind,
    int Ordinal,
    string ContentType,
    long SizeBytes,
    string ObjectKey,
    string PipelineVersion
);

/// <summary>
/// Live view of the object in Backblaze, checked at call time rather than read
/// from the database, so a model can tell metadata drift from real data loss.
/// </summary>
public sealed record StoredObjectStatus(
    bool Exists,
    long? SizeBytes,
    string? ContentType,
    string? ETag,
    string? VersionId,
    DateTime? LastModifiedUtc,
    bool SizeMatchesDatabase
);

public sealed record FileMetadata(
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string FileName,
    string FileExtension,
    string SourcePath,
    string SourceType,
    string DeviceCode,
    string Department,
    string Classification,
    long SizeBytes,
    string DeclaredContentType,
    string DetectedContentType,
    string Sha256,
    string BucketName,
    string ObjectKey,
    string B2VersionId,
    string ObjectETag,
    DateTime? SourceModifiedAtUtc,
    string ExtractionStatus,
    string ExtractionPipelineVersion,
    int SectionCount,
    int? PageCount,
    string ExtractionMetadataJson,
    string ExtractionErrorCode,
    string ExtractionError,
    DateTime? ExtractedAtUtc,
    StoredObjectStatus Storage,
    IReadOnlyList<DerivativeSummary> Derivatives
);

public sealed record DownloadTicket(
    string TargetKind,
    Guid TargetId,
    string FileName,
    string ObjectKey,
    long SizeBytes,
    string ContentType,
    string Sha256,
    string DownloadUrl,
    DateTime ExpiresAtUtc
);

public sealed record InlineFileContent(
    string TargetKind,
    Guid TargetId,
    string FileName,
    string ContentType,
    long ObjectSizeBytes,
    long BytesRead,
    bool Truncated,
    [property: JsonPropertyName("encoding")] string ContentEncoding,
    string Content
);

public sealed record DeviceSummary(
    string DeviceCode,
    string Nickname,
    string Hostname,
    string Username,
    string AgentVersion,
    string Status,
    bool IsActive,
    DateTime? LastSeenAtUtc,
    DateTime RegisteredAtUtc,
    int DocumentCount,
    long DocumentSizeBytes
);

public sealed record IngestionRecord(
    string SourceType,
    Guid SourceRecordId,
    Guid? DocumentId,
    string DeviceCode,
    string FileName,
    string Extension,
    string FullPath,
    long SizeBytes,
    string Status,
    string ErrorMessage,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc
);

public sealed record CountByKey(string Key, int Count);

public sealed record ExtractionFailure(
    Guid VersionId,
    Guid DocumentId,
    string FileName,
    string JobStatus,
    int AttemptCount,
    int MaxAttempts,
    string ErrorCode,
    string ErrorMessage,
    DateTime? NextAttemptAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record ExtractionOverview(
    IReadOnlyList<CountByKey> JobsByStatus,
    IReadOnlyList<CountByKey> VersionsByExtractionStatus,
    int QueuedJobCount,
    int RunningJobCount,
    int DeadLetterJobCount,
    IReadOnlyList<ExtractionFailure> RecentFailures
);

/// <summary>
/// One row of a breakdown. FileCount counts stored file versions rather than
/// documents, so a file revised three times contributes three.
/// </summary>
public sealed record GroupStat(string Key, int FileCount, long SizeBytes);

public sealed record StorageStats(
    int DocumentCount,
    int VersionCount,
    long TotalSizeBytes,
    IReadOnlyList<GroupStat> ByExtension,
    IReadOnlyList<GroupStat> ByDevice,
    IReadOnlyList<GroupStat> ByDepartment,
    IReadOnlyList<GroupStat> BySourceType
);

public sealed record RequeueResult(
    Guid VersionId,
    Guid DocumentId,
    string FileName,
    string PreviousJobStatus,
    string JobStatus,
    int AttemptCount,
    string Message
);
