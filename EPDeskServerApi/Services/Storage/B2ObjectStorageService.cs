using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EPDeskServerApi.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Services.Storage;

/// <summary>
/// Backblaze B2 implementation using its S3-compatible API.
/// Permanent B2 credentials remain on the server.
/// </summary>
public sealed class B2ObjectStorageService :
    IObjectStorageService,
    IDisposable
{
    private readonly IAmazonS3 _s3Client;
    private readonly B2StorageOptions _options;
    private readonly ILogger<B2ObjectStorageService> _logger;

    public B2ObjectStorageService(
        IOptions<B2StorageOptions> options,
        ILogger<B2ObjectStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        ValidateOptions(_options);

        var credentials = new BasicAWSCredentials(
            _options.KeyId,
            _options.ApplicationKey
        );

        var s3Config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl.TrimEnd('/'),

            // Used when creating AWS Signature Version 4 requests.
            AuthenticationRegion = _options.Region,

            // Generates URLs such as:
            // https://s3.region.backblazeb2.com/bucket/object
            ForcePathStyle = true
        };

        _s3Client = new AmazonS3Client(
            credentials,
            s3Config
        );
    }

    public async Task CheckConnectionAsync(
    CancellationToken cancellationToken = default)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,

            // We only need one result to verify access.
            MaxKeys = 1
        };

        var response = await _s3Client.ListObjectsV2Async(
            request,
            cancellationToken
        );

        if (response.HttpStatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Backblaze connection test returned HTTP " +
                $"{(int)response.HttpStatusCode}."
            );
        }

        _logger.LogInformation(
            "Backblaze B2 connection test succeeded. Bucket: {BucketName}",
            _options.BucketName
        );
    }

    public async Task<MultipartUploadInfo> StartMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = "application/octet-stream";
        }

        var request = new InitiateMultipartUploadRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            ContentType = contentType
        };

        var response = await _s3Client.InitiateMultipartUploadAsync(
            request,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(response.UploadId))
        {
            throw new InvalidOperationException(
                "Backblaze did not return a multipart upload ID."
            );
        }

        _logger.LogInformation(
            "B2 multipart upload started. ObjectKey: {ObjectKey}, UploadId: {UploadId}",
            objectKey,
            response.UploadId
        );

        return new MultipartUploadInfo(
            objectKey,
            response.UploadId
        );
    }

    public async Task<Uri> CreateUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateUploadId(uploadId);
        ValidatePartNumber(partNumber);
        ValidateExpiry(validFor);

        cancellationToken.ThrowIfCancellationRequested();

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            UploadId = uploadId,
            PartNumber = partNumber,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(validFor)
        };

        var url = await _s3Client.GetPreSignedURLAsync(request);

        cancellationToken.ThrowIfCancellationRequested();

        return new Uri(url, UriKind.Absolute);
    }

    public async Task<IReadOnlyList<UploadedPartInfo>>
        ListUploadedPartsAsync(
            string objectKey,
            string uploadId,
            CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateUploadId(uploadId);

        var uploadedParts = new List<UploadedPartInfo>();

        string? partNumberMarker = null;

        do
        {
            var request = new ListPartsRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                UploadId = uploadId,
                PartNumberMarker = partNumberMarker
            };

            var response = await _s3Client.ListPartsAsync(
                request,
                cancellationToken
            );

            // In AWS SDK version 4, collection properties can be null.
            if (response.Parts is not null)
            {
                foreach (var part in response.Parts)
                {
                    if (!part.PartNumber.HasValue)
                    {
                        continue;
                    }

                    uploadedParts.Add(
                        new UploadedPartInfo(
                            part.PartNumber.Value,
                            part.Size ?? 0,
                            part.ETag ?? string.Empty
                        )
                    );
                }
            }

            if (response.IsTruncated == true &&
                response.NextPartNumberMarker.HasValue)
            {
                partNumberMarker =
                    response.NextPartNumberMarker.Value.ToString(
                        CultureInfo.InvariantCulture
                    );
            }
            else
            {
                partNumberMarker = null;
            }
        }
        while (partNumberMarker is not null);

        return uploadedParts
            .OrderBy(part => part.PartNumber)
            .ToList();
    }

    public async Task<UploadedPartInfo> UploadPartFromFileAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        string filePath,
        long offsetBytes,
        long lengthBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateUploadId(uploadId);
        ValidatePartNumber(partNumber);

        if (offsetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetBytes));
        }

        if (lengthBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthBytes));
        }

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Upload source file was not found.", filePath);
        }

        if (offsetBytes + lengthBytes > fileInfo.Length)
        {
            throw new IOException("The requested upload part exceeds the file length.");
        }

        var request = new UploadPartRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            UploadId = uploadId,
            PartNumber = partNumber,
            PartSize = lengthBytes,
            FilePath = filePath,
            FilePosition = offsetBytes
        };

        var response = await _s3Client.UploadPartAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(response.ETag))
        {
            throw new InvalidOperationException(
                $"Backblaze did not return an ETag for part {partNumber}."
            );
        }

        return new UploadedPartInfo(partNumber, lengthBytes, response.ETag);
    }

    public async Task<CompletedObjectInfo> CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyCollection<UploadedPartInfo> parts,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateUploadId(uploadId);

        if (parts is null || parts.Count == 0)
        {
            throw new ArgumentException(
                "At least one uploaded part is required.",
                nameof(parts)
            );
        }

        foreach (var part in parts)
        {
            ValidatePartNumber(part.PartNumber);

            if (string.IsNullOrWhiteSpace(part.ETag))
            {
                throw new ArgumentException(
                    $"ETag is missing for part {part.PartNumber}.",
                    nameof(parts)
                );
            }
        }

        var partETags = parts
            .OrderBy(part => part.PartNumber)
            .Select(part => new PartETag
            {
                PartNumber = part.PartNumber,
                ETag = part.ETag
            })
            .ToList();

        var request = new CompleteMultipartUploadRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            UploadId = uploadId,
            PartETags = partETags
        };

        var response = await _s3Client.CompleteMultipartUploadAsync(
            request,
            cancellationToken
        );

        _logger.LogInformation(
            "B2 multipart upload completed. ObjectKey: {ObjectKey}, Parts: {PartCount}",
            objectKey,
            parts.Count
        );

        return new CompletedObjectInfo(
            objectKey,
            response.VersionId ?? "",
            response.ETag?.Trim('"') ?? ""
        );
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateUploadId(uploadId);

        var request = new AbortMultipartUploadRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            UploadId = uploadId
        };

        await _s3Client.AbortMultipartUploadAsync(
            request,
            cancellationToken
        );

        _logger.LogWarning(
            "B2 multipart upload aborted. ObjectKey: {ObjectKey}, UploadId: {UploadId}",
            objectKey,
            uploadId
        );
    }

    public async Task<Uri> CreateDownloadUrlAsync(
        string objectKey,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ValidateExpiry(validFor);

        cancellationToken.ThrowIfCancellationRequested();

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor)
        };

        var url = await _s3Client.GetPreSignedURLAsync(request);

        cancellationToken.ThrowIfCancellationRequested();

        return new Uri(url, UriKind.Absolute);
    }

    public async Task<bool> ObjectExistsAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        try
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey
            };

            await _s3Client.GetObjectMetadataAsync(
                request,
                cancellationToken
            );

            return true;
        }
        catch (AmazonS3Exception exception)
            when (IsNotFound(exception))
        {
            return false;
        }
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(
                exception.ErrorCode,
                "NotFound",
                StringComparison.OrdinalIgnoreCase
            ) ||
            string.Equals(
                exception.ErrorCode,
                "NoSuchKey",
                StringComparison.OrdinalIgnoreCase
            );
    }

    public async Task<ObjectMetadataInfo?> GetObjectMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        try
        {
            var response = await _s3Client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey
                },
                cancellationToken
            );

            return new ObjectMetadataInfo(
                objectKey,
                response.ContentLength,
                response.Headers.ContentType ?? "application/octet-stream",
                response.ETag?.Trim('"') ?? "",
                response.VersionId ?? "",
                response.LastModified?.ToUniversalTime()
            );
        }
        catch (AmazonS3Exception exception)
            when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task<ObjectReadResult> OpenReadAsync(
        string objectKey,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                "When supplied, maxBytes must be greater than zero."
            );
        }

        var request = new GetObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey
        };

        if (maxBytes.HasValue)
        {
            // A byte range is inclusive on both ends, so the last wanted
            // offset is one less than the number of bytes requested.
            request.ByteRange = new ByteRange(0, maxBytes.Value - 1);
        }

        var response = await _s3Client.GetObjectAsync(
            request,
            cancellationToken
        );

        return new ObjectReadResult(
            response.ResponseStream,
            response.ContentLength,
            response.Headers.ContentType ?? "application/octet-stream"
        );
    }

    public async Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        var request = new DeleteObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey
        };

        await _s3Client.DeleteObjectAsync(
            request,
            cancellationToken
        );

        _logger.LogInformation(
            "B2 object deleted. ObjectKey: {ObjectKey}",
            objectKey
        );
    }

    public void Dispose()
    {
        _s3Client.Dispose();
    }

    private static void ValidateOptions(
        B2StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            throw new InvalidOperationException(
                "B2:ServiceUrl is missing."
            );
        }

        if (!Uri.TryCreate(
                options.ServiceUrl,
                UriKind.Absolute,
                out var serviceUri))
        {
            throw new InvalidOperationException(
                "B2:ServiceUrl is not a valid URL."
            );
        }

        if (!string.Equals(
                serviceUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "B2:ServiceUrl must use HTTPS."
            );
        }

        if (string.IsNullOrWhiteSpace(options.Region))
        {
            throw new InvalidOperationException(
                "B2:Region is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            throw new InvalidOperationException(
                "B2:BucketName is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(options.KeyId))
        {
            throw new InvalidOperationException(
                "B2:KeyId is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationKey))
        {
            throw new InvalidOperationException(
                "B2:ApplicationKey is missing."
            );
        }

        if (options.PartSizeBytes < 5 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "B2:PartSizeBytes must be at least 5 MiB."
            );
        }

        if (options.PresignedUrlMinutes <= 0)
        {
            throw new InvalidOperationException(
                "B2:PresignedUrlMinutes must be greater than zero."
            );
        }

        if (options.DownloadUrlMinutes <= 0)
        {
            throw new InvalidOperationException(
                "B2:DownloadUrlMinutes must be greater than zero."
            );
        }
    }

    private static void ValidateObjectKey(
        string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException(
                "Object key is required.",
                nameof(objectKey)
            );
        }

        if (objectKey.StartsWith('/'))
        {
            throw new ArgumentException(
                "Object key must not start with '/'.",
                nameof(objectKey)
            );
        }
    }

    private static void ValidateUploadId(
        string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new ArgumentException(
                "Upload ID is required.",
                nameof(uploadId)
            );
        }
    }

    private static void ValidatePartNumber(
        int partNumber)
    {
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partNumber),
                "Part number must be between 1 and 10,000."
            );
        }
    }

    private static void ValidateExpiry(
        TimeSpan validFor)
    {
        if (validFor <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validFor),
                "URL validity must be greater than zero."
            );
        }

        if (validFor > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(validFor),
                "A presigned URL cannot be valid for more than seven days."
            );
        }
    }
}
