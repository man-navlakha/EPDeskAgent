namespace EPDeskServerApi.Services.Storage;

/// <summary>
/// Information returned after starting a multipart upload.
/// </summary>
public sealed record MultipartUploadInfo(
    string ObjectKey,
    string UploadId
);

/// <summary>
/// Information about one successfully uploaded part.
/// </summary>
public sealed record UploadedPartInfo(
    int PartNumber,
    long SizeBytes,
    string ETag
);

/// <summary>
/// Identity returned by object storage after a multipart upload is finalized.
/// ETag is an object-version identifier, not a SHA-256 content checksum.
/// </summary>
public sealed record CompletedObjectInfo(
    string ObjectKey,
    string VersionId,
    string ETag
);

/// <summary>
/// Head-request result describing a stored object without transferring it.
/// </summary>
public sealed record ObjectMetadataInfo(
    string ObjectKey,
    long SizeBytes,
    string ContentType,
    string ETag,
    string VersionId,
    DateTime? LastModifiedUtc
);

/// <summary>
/// An open read stream over a stored object. The caller owns
/// <see cref="Content"/> and must dispose it.
/// </summary>
public sealed record ObjectReadResult(
    Stream Content,
    long SizeBytes,
    string ContentType
) : IDisposable
{
    public void Dispose()
    {
        Content.Dispose();
    }
}
