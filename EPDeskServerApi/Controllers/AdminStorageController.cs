using Amazon.S3;
using EPDeskServerApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/storage")]
public sealed class AdminStorageController : ControllerBase
{
    private readonly IObjectStorageService _storageService;
    private readonly ILogger<AdminStorageController> _logger;

    public AdminStorageController(
        IObjectStorageService storageService,
        ILogger<AdminStorageController> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the EP-DESK API can access
    /// the configured private Backblaze bucket.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> GetStorageHealth(
        CancellationToken cancellationToken)
    {
        try
        {
            await _storageService.CheckConnectionAsync(
                cancellationToken
            );

            return Ok(new
            {
                success = true,
                provider = "backblaze_b2",
                status = "connected",
                checkedAtUtc = DateTime.UtcNow
            });
        }
        catch (AmazonS3Exception exception)
        {
            _logger.LogError(
                exception,
                "Backblaze B2 connection test failed. " +
                "ErrorCode: {ErrorCode}, StatusCode: {StatusCode}",
                exception.ErrorCode,
                exception.StatusCode
            );

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    success = false,
                    provider = "backblaze_b2",
                    status = "unavailable",
                    errorCode = exception.ErrorCode,
                    httpStatusCode = (int)exception.StatusCode,
                    checkedAtUtc = DateTime.UtcNow
                }
            );
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Storage connection test failed."
            );

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    success = false,
                    provider = "backblaze_b2",
                    status = "unavailable",
                    errorCode = "STORAGE_CONNECTION_FAILED",
                    checkedAtUtc = DateTime.UtcNow
                }
            );
        }
    }
}