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