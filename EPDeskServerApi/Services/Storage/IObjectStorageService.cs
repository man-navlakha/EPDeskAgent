namespace EPDeskServerApi.Services.Storage;

/// <summary>
/// Common contract for object-storage providers such as
/// Backblaze B2, Amazon S3, or Cloudflare R2.
/// </summary>
public interface IObjectStorageService
{
    /// <summary>
    /// Starts a new multipart upload.
    /// </summary>
    /// /// <summary>
    /// Verifies that the server can connect to the configured
    /// object-storage bucket using the current credentials.
    /// </summary>
    Task CheckConnectionAsync(
        CancellationToken cancellationToken = default
    );
    Task<MultipartUploadInfo> StartMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a temporary URL that lets the Windows agent
    /// upload one part directly to object storage.
    /// </summary>
    Task<Uri> CreateUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan validFor,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reads the parts that already exist in object storage.
    /// This is required when resuming an interrupted upload.
    /// </summary>
    Task<IReadOnlyList<UploadedPartInfo>> ListUploadedPartsAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Combines all uploaded parts into one final object.
    /// </summary>
    Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyCollection<UploadedPartInfo> parts,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Cancels an unfinished multipart upload.
    /// </summary>
    Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates a temporary private download URL.
    /// </summary>
    Task<Uri> CreateDownloadUrlAsync(
        string objectKey,
        TimeSpan validFor,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks whether the completed object exists.
    /// </summary>
    Task<bool> ObjectExistsAsync(
        string objectKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes a completed object from storage.
    /// </summary>
    Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default
    );
}