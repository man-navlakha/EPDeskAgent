using System.Buffers;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using EPDeskExtractionWorker.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Services.Processing;

public sealed class B2ObjectTransferService
{
    private const int BufferSize = 128 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly B2StorageOptions _options;

    public B2ObjectTransferService(
        IAmazonS3 s3,
        IOptions<B2StorageOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
    }

    public async Task<DownloadedObject> DownloadAsync(
        string bucketName,
        string objectKey,
        string versionId,
        string destinationPath,
        long expectedSizeBytes,
        long maximumSizeBytes,
        CancellationToken cancellationToken)
    {
        ValidateSource(bucketName, objectKey, expectedSizeBytes, maximumSizeBytes);

        var request = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey
        };

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            request.VersionId = versionId;
        }

        try
        {
            using var response = await _s3.GetObjectAsync(request, cancellationToken);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long totalBytes = 0;

            try
            {
                while (true)
                {
                    var read = await response.ResponseStream.ReadAsync(
                        buffer.AsMemory(0, BufferSize),
                        cancellationToken
                    );
                    if (read == 0)
                    {
                        break;
                    }

                    totalBytes = checked(totalBytes + read);
                    if (totalBytes > maximumSizeBytes || totalBytes > expectedSizeBytes)
                    {
                        throw new RejectedExtractionException(
                            "source_too_large",
                            $"Downloaded object exceeded its allowed size ({maximumSizeBytes} bytes)."
                        );
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken);
            if (totalBytes != expectedSizeBytes)
            {
                throw new RetryableExtractionException(
                    "source_size_mismatch",
                    $"Expected {expectedSizeBytes} bytes but downloaded {totalBytes} bytes."
                );
            }

            return new DownloadedObject(
                destinationPath,
                totalBytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                response.VersionId ?? "",
                response.ETag?.Trim('"') ?? ""
            );
        }
        catch (ExtractionProcessingException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(destinationPath);
            throw new RetryableExtractionException(
                "b2_download_failed",
                "The private B2 object could not be downloaded.",
                exception
            );
        }
    }

    public async Task<UploadedObject> UploadDerivativeAsync(
        string objectKey,
        string filePath,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            !objectKey.StartsWith("derivatives/", StringComparison.Ordinal) ||
            objectKey.StartsWith('/') ||
            objectKey.Split('/').Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException("Derivative object key is unsafe.");
        }

        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Derivative file does not exist.", filePath);
        }

        var sha256 = await CalculateSha256Async(filePath, cancellationToken);
        try
        {
            var response = await _s3.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                    FilePath = filePath,
                    ContentType = string.IsNullOrWhiteSpace(contentType)
                        ? "application/octet-stream"
                        : contentType
                },
                cancellationToken
            );

            return new UploadedObject(
                _options.BucketName,
                objectKey,
                response.VersionId ?? "",
                response.ETag?.Trim('"') ?? "",
                file.Length,
                sha256,
                contentType
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RetryableExtractionException(
                "b2_derivative_upload_failed",
                "The structured extraction derivative could not be uploaded to B2.",
                exception
            );
        }
    }

    private static async Task<string> CalculateSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateSource(
        string bucketName,
        string objectKey,
        long expectedSizeBytes,
        long maximumSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(objectKey))
        {
            throw new RejectedExtractionException(
                "source_metadata_missing",
                "The queued document has incomplete B2 source metadata."
            );
        }

        if (expectedSizeBytes <= 0 || maximumSizeBytes <= 0 || expectedSizeBytes > maximumSizeBytes)
        {
            throw new RejectedExtractionException(
                "source_too_large",
                $"The queued object size {expectedSizeBytes} is outside the configured limit."
            );
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The workspace disposer makes a final cleanup attempt.
        }
    }
}

public sealed record DownloadedObject(
    string FilePath,
    long SizeBytes,
    string Sha256,
    string VersionId,
    string ETag
);

public sealed record UploadedObject(
    string BucketName,
    string ObjectKey,
    string VersionId,
    string ETag,
    long SizeBytes,
    string Sha256,
    string ContentType
);
