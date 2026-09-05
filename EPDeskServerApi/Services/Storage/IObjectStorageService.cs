namespace EPDeskServerApi.Services.Storage;

/// <summary>
/// Common contract for object-storage providers such as
/// Backblaze B2, Amazon S3, or Cloudflare R2.
/// </summary>
public interface IObjectStorageService
{
    /// <summary>
    /// Verifies that the server can connect to the configured
    /// object-storage bucket using the current credentials.
    /// </summary>
    Task CheckConnectionAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Starts a new multipart upload.
    /// </summary>
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
    /// Uploads one part from a file that is locally accessible to the API host.
    /// </summary>
    Task<UploadedPartInfo> UploadPartFromFileAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        string filePath,
        long offsetBytes,
        long lengthBytes,
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
    Task<CompletedObjectInfo> CompleteMultipartUploadAsync(
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
    /// Reads the stored object's headers without transferring its body.
    /// Returns null when the object does not exist.
    /// </summary>
    Task<ObjectMetadataInfo?> GetObjectMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Opens a read stream over a stored object. Supplying
    /// <paramref name="maxBytes"/> requests only the leading bytes, which keeps
    /// previews of very large objects cheap.
    /// </summary>
    Task<ObjectReadResult> OpenReadAsync(
        string objectKey,
        long? maxBytes = null,
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
