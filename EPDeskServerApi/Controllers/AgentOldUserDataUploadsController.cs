using Amazon.S3;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Security;
using EPDeskServerApi.Services;
using EPDeskServerApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

namespace EPDeskServerApi.Controllers;

/// <summary>
/// Lets a desktop tool push an archived "Users Data" folder into the old-user-data
/// corpus, for the case the server-side importer cannot cover: the archive drive is
/// attached to someone's PC rather than readable by this API host.
///
/// It is deliberately separate from <see cref="AgentFileUploadsController"/>. That
/// controller is what every staff laptop's agent calls, and archived data wants a
/// different object layout and a different source type, so the two are kept apart
/// rather than one being bent to serve both.
///
/// Objects land under the same readable layout the server-side importer uses:
/// <c>uploads/old-user-data/{person}/{relative path}</c>.
/// </summary>
[ApiController]
[Route("api/agent/old-user-data")]
[ServiceFilter<AgentFileUploadApiKeyAuthorizationFilter>]
public sealed class AgentOldUserDataUploadsController : ControllerBase
{
    /// <summary>
    /// Marks a job as fed by a client rather than scanned from this host's disk.
    /// <see cref="OldUserDataImportWorker"/> only claims pending, scanning and
    /// uploading jobs, so this status keeps it from trying to read a drive that is
    /// on someone else's desk.
    /// </summary>
    private const string ClientPushStatus = "client_push";

    private readonly AppDbContext _db;
    private readonly FileUploadPolicyService _policyService;
    private readonly IObjectStorageService _storageService;
    private readonly DocumentExtractionQueueService _extractionQueue;
    private readonly B2StorageOptions _storageOptions;
    private readonly OldUserDataImportOptions _importOptions;
    private readonly ILogger<AgentOldUserDataUploadsController> _logger;

    public AgentOldUserDataUploadsController(
        AppDbContext db,
        FileUploadPolicyService policyService,
        IObjectStorageService storageService,
        DocumentExtractionQueueService extractionQueue,
        IOptions<B2StorageOptions> storageOptions,
        IOptions<OldUserDataImportOptions> importOptions,
        ILogger<AgentOldUserDataUploadsController> logger)
    {
        _db = db;
        _policyService = policyService;
        _storageService = storageService;
        _extractionQueue = extractionQueue;
        _storageOptions = storageOptions.Value;
        _importOptions = importOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns the job that owns one archive root, creating it on first use.
    /// Files hang off a job row, and the client has no other way to obtain one.
    /// </summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> GetOrCreateJob(
        CreateOldUserDataPushJobDto dto,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.RootPath))
        {
            return BadRequest("Root path is required.");
        }

        var rootPath = dto.RootPath.Trim()
            .TrimEnd('\\', '/');
        var rootIdentity = rootPath.ToUpperInvariant();

        var existing = await _db.OldUserDataImportJobs
            .FirstOrDefaultAsync(x => x.RootPathIdentity == rootIdentity, cancellationToken);

        if (existing != null)
        {
            return Ok(ToJobResponse(existing));
        }

        var job = new OldUserDataImportJob
        {
            Id = Guid.NewGuid(),
            RootPath = rootPath,
            RootPathIdentity = rootIdentity,
            SourceLabel = string.IsNullOrWhiteSpace(dto.SourceLabel)
                ? "Old User Data"
                : dto.SourceLabel.Trim(),
            Status = ClientPushStatus,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.OldUserDataImportJobs.Add(job);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two machines can start on the same root at once; the unique index
            // decides, and the loser reads back the winner's row.
            _db.Entry(job).State = EntityState.Detached;

            existing = await _db.OldUserDataImportJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.RootPathIdentity == rootIdentity, cancellationToken);

            if (existing == null)
            {
                throw;
            }

            return Ok(ToJobResponse(existing));
        }

        return Ok(ToJobResponse(job));
    }

    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate(
        InitiateOldUserDataUploadDto dto,
        CancellationToken cancellationToken)
    {
        if (dto.JobId == Guid.Empty)
        {
            return BadRequest("Job id is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.UserFolder) ||
            string.IsNullOrWhiteSpace(dto.RelativePath) ||
            string.IsNullOrWhiteSpace(dto.FileName))
        {
            return BadRequest("User folder, relative path and file name are required.");
        }

        if (dto.SizeBytes <= 0)
        {
            return BadRequest("Only non-empty files can be uploaded.");
        }

        var jobExists = await _db.OldUserDataImportJobs
            .AnyAsync(x => x.Id == dto.JobId, cancellationToken);

        if (!jobExists)
        {
            return NotFound("Import job not found.");
        }

        var policy = await _policyService.GetOrCreateAsync(cancellationToken);

        if (!policy.IsEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "File upload is disabled.");
        }

        var extension = NormalizeExtension(dto.Extension, dto.FileName);

        if (!FileUploadPolicyService.ReadExtensions(policy)
                .Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest($"Extension '{extension}' is not enabled for upload.");
        }

        if (dto.SizeBytes > policy.MaxFileSizeBytes)
        {
            return BadRequest(
                $"File exceeds the configured limit of {policy.MaxFileSizeBytes} bytes."
            );
        }

        if (dto.SizeBytes > _storageOptions.PartSizeBytes * 10_000L)
        {
            return BadRequest("File requires more than the maximum 10,000 upload parts.");
        }

        var sha256 = NormalizeSha256(dto.Sha256);

        if (sha256.Length > 0 && (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)))
        {
            return BadRequest("SHA256 must contain exactly 64 hexadecimal characters.");
        }

        var userFolder = dto.UserFolder.Trim();
        var relativePath = dto.RelativePath.Trim().Replace('\\', '/').TrimStart('/');
        var fileName = SanitizeFileName(dto.FileName);
        var fullPath = string.IsNullOrWhiteSpace(dto.FullPath)
            ? $"{userFolder}/{relativePath}"
            : dto.FullPath.Trim();
        var lastModifiedAtUtc = ForceUtc(dto.LastModifiedAtUtc);

        var record = await _db.OldUserDataFiles.FirstOrDefaultAsync(
            x => x.ImportJobId == dto.JobId && x.FullPath == fullPath,
            cancellationToken
        );

        var sameVersion = record != null &&
                          record.SizeBytes == dto.SizeBytes &&
                          record.UpdatedAtUtc == lastModifiedAtUtc &&
                          (sha256.Length == 0 ||
                           record.Sha256.Length == 0 ||
                           string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

        if (sameVersion && record!.Status == "completed")
        {
            await using var completedTransaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            if (sha256.Length > 0)
            {
                record.Sha256 = sha256;
            }

            await _extractionQueue.EnsureOldUserDataFileQueuedAsync(record, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await completedTransaction.CommitAsync(cancellationToken);

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

        if (sameVersion && !string.IsNullOrWhiteSpace(record!.MultipartUploadId))
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
                await _db.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    shouldUpload = true,
                    uploadId = record.Id,
                    record.Status,
                    record.ObjectKey,
                    record.PartSizeBytes,
                    expectedPartCount =
                        GetExpectedPartCount(record.SizeBytes, record.PartSizeBytes),
                    uploadedPartNumbers = uploadedParts.Select(x => x.PartNumber).ToArray()
                });
            }
            catch (AmazonS3Exception exception) when (IsMissingMultipartUpload(exception))
            {
                // Backblaze expires unfinished uploads; start a fresh one below.
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
                    "Could not abort superseded old-user-data upload {UploadRecordId}.",
                    record.Id
                );
            }
        }

        var objectKey = CreateObjectKey(userFolder, relativePath);
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
                record = new OldUserDataFile
                {
                    Id = Guid.NewGuid(),
                    ImportJobId = dto.JobId,
                    FullPath = fullPath,
                    IndexedAtUtc = DateTime.UtcNow
                };

                _db.OldUserDataFiles.Add(record);
            }

            record.UserFolder = userFolder;
            record.DeviceCode = CreateDeviceCode(dto.DeviceCode, userFolder);
            record.RelativePath = relativePath;
            record.FileName = fileName;
            record.Extension = extension;
            record.SizeBytes = dto.SizeBytes;
            record.CreatedAtUtc = ForceUtc(dto.CreatedAtUtc);
            record.UpdatedAtUtc = lastModifiedAtUtc;
            record.ContentType = contentType;
            record.ObjectKey = multipart.ObjectKey;
            record.B2VersionId = "";
            record.ObjectETag = "";
            record.Sha256 = sha256;
            record.MultipartUploadId = multipart.UploadId;
            record.PartSizeBytes = _storageOptions.PartSizeBytes;
            record.UploadedBytes = 0;
            record.Status = "uploading";
            record.ErrorMessage = "";
            record.UploadStartedAtUtc = DateTime.UtcNow;
            record.CompletedAtUtc = null;

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

    [HttpPost("{id:guid}/parts/{partNumber:int}/url")]
    public async Task<IActionResult> CreatePartUrl(
        Guid id,
        int partNumber,
        CancellationToken cancellationToken)
    {
        var record = await _db.OldUserDataFiles
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
        var record = await _db.OldUserDataFiles
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status == "completed")
        {
            await using var completedTransaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            var already = await _extractionQueue.EnsureOldUserDataFileQueuedAsync(
                record,
                cancellationToken
            );

            await _db.SaveChangesAsync(cancellationToken);
            await completedTransaction.CommitAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                uploadId = record.Id,
                record.Status,
                extractionJobId = already.ExtractionJobId,
                extractionQueued = already.JobCreated
            });
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

        var completedObject = await _storageService.CompleteMultipartUploadAsync(
            record.ObjectKey,
            record.MultipartUploadId,
            parts,
            cancellationToken
        );

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        record.Status = "completed";
        record.ErrorMessage = "";
        record.B2VersionId = completedObject.VersionId;
        record.ObjectETag = completedObject.ETag;
        record.UploadedBytes = record.SizeBytes;
        record.CompletedAtUtc = DateTime.UtcNow;
        record.MultipartUploadId = "";
        record.LeaseUntilUtc = null;

        var queueResult = await _extractionQueue.EnsureOldUserDataFileQueuedAsync(
            record,
            cancellationToken
        );

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            uploadId = record.Id,
            record.Status,
            record.ObjectKey,
            record.CompletedAtUtc,
            extractionJobId = queueResult.ExtractionJobId,
            extractionQueued = queueResult.JobCreated
        });
    }

    [HttpPost("{id:guid}/failure")]
    public async Task<IActionResult> ReportFailure(
        Guid id,
        OldUserDataUploadFailureDto dto,
        CancellationToken cancellationToken)
    {
        var record = await _db.OldUserDataFiles
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (record == null)
        {
            return NotFound("File upload not found.");
        }

        if (record.Status != "completed")
        {
            // The multipart upload is left open so the next attempt resumes from
            // the parts Backblaze already holds.
            record.Status = "uploading";
            record.ErrorMessage = dto.ErrorMessage?.Trim() ?? "";
            record.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { success = true, uploadId = record.Id, record.Status });
    }

    // ------------------------------------------------------------------ helpers

    private string CreateObjectKey(string userFolder, string relativePath)
    {
        var prefix = _importOptions.ObjectKeyPrefix.Trim().Trim('/');

        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "uploads/old-user-data";
        }

        var safeUserFolder = SanitizePathSegment(userFolder);
        var safeRelativePath = string.Join(
            '/',
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x != "." && x != "..")
                .Select(SanitizePathSegment)
        );

        return $"{prefix}/{safeUserFolder}/{safeRelativePath}";
    }

    private static string CreateDeviceCode(string? provided, string userFolder)
    {
        var value = string.IsNullOrWhiteSpace(provided) ? userFolder : provided;

        var sanitized = new string(value.Trim().ToUpperInvariant()
            .Select(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.' ? x : '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "OLD_USER" : sanitized;
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = new string(value.Trim()
            .Select(x => char.IsControl(x) || x is '/' or '\\' ? '_' : x)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string? ValidateParts(
        OldUserDataFile record,
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

    private static int GetExpectedPartCount(long sizeBytes, long partSizeBytes) =>
        checked((int)((sizeBytes + partSizeBytes - 1) / partSizeBytes));

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
        return new DateTime(utcValue.Ticks - (utcValue.Ticks % 10), DateTimeKind.Utc);
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

    private static string NormalizeSha256(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static bool IsMissingMultipartUpload(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase);

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

    private static object ToJobResponse(OldUserDataImportJob job) => new
    {
        jobId = job.Id,
        job.SourceLabel,
        job.Status,
        job.RootPath,
        job.CreatedAtUtc
    };
}

public sealed class CreateOldUserDataPushJobDto
{
    public string RootPath { get; set; } = "";
    public string SourceLabel { get; set; } = "";
}

public sealed class InitiateOldUserDataUploadDto
{
    public Guid JobId { get; set; }

    /// <summary>The top-level person folder, which becomes the first path segment.</summary>
    public string UserFolder { get; set; } = "";

    /// <summary>Path below the person folder, kept as-is in the object key.</summary>
    public string RelativePath { get; set; } = "";

    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastModifiedAtUtc { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class OldUserDataUploadFailureDto
{
    public string ErrorMessage { get; set; } = "";
}
