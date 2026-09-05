namespace EPDeskOldDataUploader.Models;

/// <summary>
/// The extensions and size ceiling the server currently accepts. Fetched once per
/// run so this tool can never push a file the API would reject anyway.
/// </summary>
public sealed class UploadPolicy
{
    public bool IsEnabled { get; set; }
    public List<string> Extensions { get; set; } = [];
    public long MaxFileSizeBytes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InitiateUploadRequest
{
    public string DeviceCode { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModifiedAtUtc { get; set; }
    public string Sha256 { get; set; } = "";

    // Only sent in readable-folder mode, where they become the object key's
    // path: uploads/old-user-data/{UserFolder}/{RelativePath}.
    public Guid JobId { get; set; }
    public string UserFolder { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class OldUserDataJobResponse
{
    public Guid JobId { get; set; }
    public string SourceLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public string RootPath { get; set; } = "";
}

public sealed class InitiateUploadResponse
{
    /// <summary>
    /// False when the server already holds this exact revision. The file is then
    /// counted as already-present instead of being sent a second time.
    /// </summary>
    public bool ShouldUpload { get; set; }

    public Guid UploadId { get; set; }
    public string Status { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public long PartSizeBytes { get; set; }
    public int ExpectedPartCount { get; set; }

    /// <summary>
    /// Parts Backblaze already holds from an earlier attempt. Resuming skips them.
    /// </summary>
    public List<int> UploadedPartNumbers { get; set; } = [];
}

public sealed class UploadPartUrlResponse
{
    public Guid UploadId { get; set; }
    public int PartNumber { get; set; }
    public string UploadUrl { get; set; } = "";
    public long OffsetBytes { get; set; }
    public long LengthBytes { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public enum FileState
{
    Pending,
    Uploading,
    Completed,
    AlreadyOnServer,
    Skipped,
    Failed
}

public sealed class ScannedFile
{
    public required string UserFolder { get; init; }

    /// <summary>
    /// Settable so editing the prefix after a scan re-labels what is already
    /// listed. Rescanning a large archive costs minutes; relabelling is instant.
    /// </summary>
    public required string DeviceCode { get; set; }

    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModifiedAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    public FileState State { get; set; } = FileState.Pending;
    public string Message { get; set; } = "";
    public int AttemptCount { get; set; }
}

public sealed class ScannedUser
{
    public required string UserFolder { get; init; }
    public required string DeviceCode { get; set; }
    public List<ScannedFile> Files { get; } = [];

    /// <summary>
    /// Ticked in the grid. Only selected folders are queued, so a large archive
    /// can go up a few people at a time instead of all at once. Off by default:
    /// with a 900 GB archive listed, nothing should start by accident.
    /// </summary>
    public bool IsSelected { get; set; }

    /// <summary>What an earlier run left behind, shown before this one starts.</summary>
    public string HistoryLabel { get; set; } = "";

    public long TotalBytes { get; set; }

    // Counted as files finish rather than recomputed from the list, because the
    // grid refreshes twice a second and the list can hold six figures of files.
    public int UploadedCount { get; set; }
    public int AlreadyPresentCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }

    public int DoneCount =>
        UploadedCount + AlreadyPresentCount + SkippedCount + FailedCount;

    public void ResetCounts()
    {
        UploadedCount = 0;
        AlreadyPresentCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
    }
}

public sealed class ScanResult
{
    public List<ScannedUser> Users { get; } = [];

    /// <summary>
    /// Files whose extension is outside the policy. They are counted for the
    /// summary line but never listed, which keeps a 100k-file archive readable.
    /// </summary>
    public long IgnoredByExtensionCount { get; set; }

    public long UnreadableFolderCount { get; set; }

    public IEnumerable<ScannedFile> AllFiles => Users.SelectMany(x => x.Files);
}
