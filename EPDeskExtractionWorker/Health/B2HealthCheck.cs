using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using EPDeskExtractionWorker.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Health;

public sealed class B2HealthCheck(
    IAmazonS3 s3Client,
    IOptions<B2StorageOptions> options
) : IHealthCheck
{
    private readonly B2StorageOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3Client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.BucketName,
                    Prefix = _options.ConnectionTestPrefix,
                    MaxKeys = 1
                },
                cancellationToken
            );

            return response.HttpStatusCode == HttpStatusCode.OK
                ? HealthCheckResult.Healthy("Backblaze B2 is reachable.")
                : HealthCheckResult.Unhealthy(
                    $"Backblaze B2 returned HTTP {(int)response.HttpStatusCode}."
                );
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Backblaze B2 connection failed.");
        }
    }
}
