using System.Security.Cryptography;
using System.Text;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using EPDeskServerApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Amazon.S3;
using System.Net;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/agent/file-uploads")]
public sealed class AgentFileUploadsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly FileUploadPolicyService _policyService;
    private readonly IObjectStorageService _storageService;
    private readonly B2StorageOptions _storageOptions;
    private readonly ILogger<AgentFileUploadsController> _logger;

    public AgentFileUploadsController(
        AppDbContext db,
        FileUploadPolicyService policyService,
        IObjectStorageService storageService,
        IOptions<B2StorageOptions> storageOptions,
        ILogger<AgentFileUploadsController> logger)
    {
        _db = db;
        _policyService = policyService;
        _storageService = storageService;
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate(
        InitiateAutomaticFileUploadDto dto,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.FullPath) ||
            string.IsNullOrWhiteSpace(dto.FileName))
        {
            return BadRequest("Full path and file name are required.");
        }

        if (dto.SizeBytes <= 0)
        {
            return BadRequest("Only non-empty files can be uploaded.");
        }

        var policy = await _policyService.GetOrCreateAsync(cancellationToken);

        if (!policy.IsEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Automatic file upload is disabled.");
        }

        var extension = NormalizeExtension(dto.Extension, dto.FileName);
        var allowedExtensions = FileUploadPolicyService.ReadExtensions(policy);

        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest($"Extension '{extension}' is not enabled for automatic upload.");
        }

        if (dto.SizeBytes > policy.MaxFileSizeBytes)
        {
            return BadRequest($"File exceeds the configured limit of {policy.MaxFileSizeBytes} bytes.");
        }

        if (dto.SizeBytes > _storageOptions.PartSizeBytes * 10_000L)
        {
            return BadRequest("File requires more than the maximum 10,000 multipart upload parts.");
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();
        var fullPath = dto.FullPath.Trim();
        var pathIdentity = CreatePathIdentity(fullPath);
        var fileName = SanitizeFileName(dto.FileName);
        var lastModifiedAtUtc = ForceUtc(dto.LastModifiedAtUtc);

        var record = await _db.AutomaticFileUploads.FirstOrDefaultAsync(
            x => x.DeviceCode == deviceCode && x.PathIdentity == pathIdentity,
            cancellationToken
        );

        var sameVersion = record != null &&
                          record.SizeBytes == dto.SizeBytes &&
                          record.LastModifiedAtUtc == lastModifiedAtUtc;

        if (sameVersion && record!.Status == "completed")
        {
            return Ok(new
            {
                shouldUpload = false,
                uploadId = record.Id,
                record.Status,
                record.ObjectKey,
                record.PartSizeBytes,
                expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes),
                uploadedPartNumbers = Array.Empty<int>()
            });
        }

        if (sameVersion &&
            record!.Status != "aborted" &&
            !string.IsNullOrWhiteSpace(record.MultipartUploadId))
        {
            try
            {
                var uploadedParts = await _storageService.ListUploadedPartsAsync(
                    record.ObjectKey,
                    record.MultipartUploadId,
                    cancellationToken
                );

                record.Status = "uploading";
                record.ErrorMessage = "";
                record.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    shouldUpload = true,
                    uploadId = record.Id,
                    record.Status,
                    record.ObjectKey,
                    record.PartSizeBytes,
                    expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes),
                    uploadedPartNumbers = uploadedParts.Select(x => x.PartNumber).ToArray()
                });
            }
            catch (AmazonS3Exception exception) when (IsMissingMultipartUpload(exception))
            {
                // Backblaze can expire an unfinished upload. Start a fresh one below.
                record.MultipartUploadId = "";
            }
        }

        if (record != null && !string.IsNullOrWhiteSpace(record.MultipartUploadId))
        {
            try
            {
                await _storageService.AbortMultipartUploadAsync(
                    record.ObjectKey,
                    record.MultipartUploadId,
                    cancellationToken
                );
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not abort superseded multipart upload {UploadRecordId}.",
                    record.Id
                );
            }
        }

        var objectKey = CreateObjectKey(deviceCode, fullPath, fileName);
        var contentType = GetContentType(extension);
        var multipart = await _storageService.StartMultipartUploadAsync(
            objectKey,
            contentType,
            cancellationToken
        );

        try
        {
            if (record == null)
            {
                record = new AutomaticFileUpload
                {
                    Id = Guid.NewGuid(),
                    DeviceCode = deviceCode,
                    FullPath = fullPath,
                    PathIdentity = pathIdentity,
                    CreatedAtUtc = DateTime.UtcNow
                };

                _db.AutomaticFileUploads.Add(record);
            }

            record.FileName = fileName;
            record.FullPath = fullPath;
            record.PathIdentity = pathIdentity;
            record.Extension = extension;
            record.SizeBytes = dto.SizeBytes;
            record.LastModifiedAtUtc = lastModifiedAtUtc;
            record.ContentType = contentType;
            record.ObjectKey = multipart.ObjectKey;
            record.MultipartUploadId = multipart.UploadId;
            record.PartSizeBytes = _storageOptions.PartSizeBytes;
            record.Status = "uploading";
            record.ErrorMessage = "";
            record.CompletedAtUtc = null;
            record.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _storageService.AbortMultipartUploadAsync(
                multipart.ObjectKey,
                multipart.UploadId,
                cancellationToken
            );

            throw;
        }

        return Ok(new
        {
            shouldUpload = true,
            uploadId = record.Id,
            record.Status,
            record.ObjectKey,
            record.PartSizeBytes,
            expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes),
            uploadedPartNumbers = Array.Empty<int>()
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken cancellationToken)
    {
        var record = await _db.AutomaticFileUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        IReadOnlyList<UploadedPartInfo> parts = [];

        if (record.Status == "uploading" &&
            !string.IsNullOrWhiteSpace(record.MultipartUploadId))
        {
            parts = await _storageService.ListUploadedPartsAsync(
                record.ObjectKey,
                record.MultipartUploadId,
                cancellationToken
            );
        }

        return Ok(new
        {
            uploadId = record.Id,
            record.Status,
            record.SizeBytes,
            record.PartSizeBytes,
            expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes),
            uploadedParts = parts,
            record.ErrorMessage,
            record.UpdatedAtUtc,
            record.CompletedAtUtc
        });
    }

    [HttpPost("{id:guid}/parts/{partNumber:int}/url")]
    public async Task<IActionResult> CreatePartUrl(
        Guid id,
        int partNumber,
        CancellationToken cancellationToken)
    {
        var record = await _db.AutomaticFileUploads
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status != "uploading")
        {
            return BadRequest($"Upload is not active. Current status: {record.Status}");
        }

        var expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes);

        if (partNumber < 1 || partNumber > expectedPartCount)
        {
            return BadRequest($"Part number must be between 1 and {expectedPartCount}.");
        }

        var validFor = TimeSpan.FromMinutes(_storageOptions.PresignedUrlMinutes);
        var url = await _storageService.CreateUploadPartUrlAsync(
            record.ObjectKey,
            record.MultipartUploadId,
            partNumber,
            validFor,
            cancellationToken
        );

        var offset = (partNumber - 1L) * record.PartSizeBytes;
        var length = Math.Min(record.PartSizeBytes, record.SizeBytes - offset);

        return Ok(new
        {
            uploadId = record.Id,
            partNumber,
            uploadUrl = url,
            offsetBytes = offset,
            lengthBytes = length,
            expiresAtUtc = DateTime.UtcNow.Add(validFor)
        });
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken)
    {
        var record = await _db.AutomaticFileUploads
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status == "completed")
        {
            return Ok(new { success = true, uploadId = record.Id, record.Status });
        }

        if (record.Status != "uploading")
        {
            return BadRequest($"Upload is not active. Current status: {record.Status}");
        }

        var parts = await _storageService.ListUploadedPartsAsync(
            record.ObjectKey,
            record.MultipartUploadId,
            cancellationToken
        );

        var expectedPartCount = GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes);
        var validationError = ValidateParts(record, parts, expectedPartCount);

        if (validationError != null)
        {
            return BadRequest(validationError);
        }

        await _storageService.CompleteMultipartUploadAsync(
            record.ObjectKey,
            record.MultipartUploadId,
            parts,
            cancellationToken
        );

        record.Status = "completed";
        record.ErrorMessage = "";
        record.CompletedAtUtc = DateTime.UtcNow;
        record.UpdatedAtUtc = DateTime.UtcNow;
        record.MultipartUploadId = "";

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            uploadId = record.Id,
            record.Status,
            record.ObjectKey,
            record.CompletedAtUtc
        });
    }

    [HttpPost("{id:guid}/failure")]
    public async Task<IActionResult> ReportFailure(
        Guid id,
        AutomaticFileUploadFailureDto dto,
        CancellationToken cancellationToken)
    {
        var record = await _db.AutomaticFileUploads
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status != "completed" && record.Status != "aborted")
        {
            // Keep the multipart upload active so the next scan can resume it.
            record.Status = "uploading";
            record.ErrorMessage = dto.ErrorMessage?.Trim() ?? "";
            record.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { success = true, uploadId = record.Id, record.Status });
    }

    [HttpPost("{id:guid}/abort")]
    public async Task<IActionResult> Abort(Guid id, CancellationToken cancellationToken)
    {
        var record = await _db.AutomaticFileUploads
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status == "uploading" &&
            !string.IsNullOrWhiteSpace(record.MultipartUploadId))
        {
            await _storageService.AbortMultipartUploadAsync(
                record.ObjectKey,
                record.MultipartUploadId,
                cancellationToken
            );
        }

        record.Status = "aborted";
        record.MultipartUploadId = "";
        record.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, uploadId = record.Id, record.Status });
    }

    private static string? ValidateParts(
        AutomaticFileUpload record,
        IReadOnlyList<UploadedPartInfo> parts,
        int expectedPartCount)
    {
        if (parts.Count != expectedPartCount)
        {
            return $"Expected {expectedPartCount} uploaded parts, but Backblaze has {parts.Count}.";
        }

        for (var partNumber = 1; partNumber <= expectedPartCount; partNumber++)
        {
            var part = parts.FirstOrDefault(x => x.PartNumber == partNumber);

            if (part == null)
            {
                return $"Uploaded part {partNumber} is missing.";
            }

            var offset = (partNumber - 1L) * record.PartSizeBytes;
            var expectedSize = Math.Min(record.PartSizeBytes, record.SizeBytes - offset);

            if (part.SizeBytes != expectedSize)
            {
                return $"Uploaded part {partNumber} has size {part.SizeBytes}; expected {expectedSize}.";
            }
        }

        return null;
    }

    private static int GetExpectedPartCount(long sizeBytes, long partSizeBytes)
    {
        return checked((int)((sizeBytes + partSizeBytes - 1) / partSizeBytes));
    }

    private static DateTime ForceUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        // PostgreSQL timestamps have microsecond precision (10 .NET ticks).
        return new DateTime(
            utcValue.Ticks - (utcValue.Ticks % 10),
            DateTimeKind.Utc
        );
    }

    private static string NormalizeExtension(string extension, string fileName)
    {
        var value = string.IsNullOrWhiteSpace(extension)
            ? Path.GetExtension(fileName)
            : extension;

        value = value.Trim().TrimStart('.');
        return value.Length == 0 ? "" : "." + value.ToLowerInvariant();
    }

    private static string SanitizeFileName(string fileName)
    {
        var value = fileName.Trim().Replace('\\', '_').Replace('/', '_');

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "file" : value;
    }

    private static string CreateObjectKey(
        string deviceCode,
        string fullPath,
        string fileName)
    {
        var safeDeviceCode = new string(deviceCode
            .Select(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.' ? x : '_')
            .ToArray());

        var normalizedPath = CreatePathIdentity(fullPath);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))
        ).ToLowerInvariant();

        return $"devices/{safeDeviceCode}/{hash}/{fileName}";
    }

    private static string CreatePathIdentity(string fullPath)
    {
        return fullPath
            .Trim()
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToUpperInvariant();
    }

    private static string GetContentType(string extension)
    {
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".doc" or ".dot" => "application/msword",
            ".docx" or ".dotx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" or ".xlt" => "application/vnd.ms-excel",
            ".xlsx" or ".xltx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv" => "text/csv",
            ".ppt" or ".pps" or ".pot" => "application/vnd.ms-powerpoint",
            ".pptx" or ".ppsx" or ".potx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".rtf" => "application/rtf",
            _ => "application/octet-stream"
        };
    }

    private static bool IsMissingMultipartUpload(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound ||
               string.Equals(
                   exception.ErrorCode,
                   "NoSuchUpload",
                   StringComparison.OrdinalIgnoreCase
               );
    }
}

public sealed class InitiateAutomaticFileUploadDto
{
    public string DeviceCode { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModifiedAtUtc { get; set; }
}

public sealed class AutomaticFileUploadFailureDto
{
    public string ErrorMessage { get; set; } = "";
}
