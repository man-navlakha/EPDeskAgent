using System.Security.Cryptography;
using System.Text;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/document-extraction")]
public sealed class AdminDocumentExtractionController : ControllerBase
{
    private const int MaximumBatchSize = 500;

    private readonly AppDbContext _db;
    private readonly DocumentExtractionQueueService _queueService;
    private readonly DocumentExtractionOptions _options;

    public AdminDocumentExtractionController(
        AppDbContext db,
        DocumentExtractionQueueService queueService,
        IOptions<DocumentExtractionOptions> options)
    {
        _db = db;
        _queueService = queueService;
        _options = options.Value;
    }

    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill(
        DocumentExtractionBackfillRequest request,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        var sourceType = request.SourceType?.Trim().ToLowerInvariant() ?? "";
        if (sourceType is not "all" and
            not DocumentExtractionQueueService.AutomaticUploadSourceType and
            not DocumentExtractionQueueService.OldUserDataSourceType)
        {
            return BadRequest(
                "SourceType must be 'all', 'automatic_upload', or 'old_user_data'."
            );
        }

        if (request.BatchSize is < 1 or > MaximumBatchSize)
        {
            return BadRequest(
                $"BatchSize must be between 1 and {MaximumBatchSize}."
            );
        }

        if (request.SourceRecordId == Guid.Empty)
        {
            return BadRequest("SourceRecordId must be a non-empty GUID when provided.");
        }

        if (request.SourceRecordId.HasValue && sourceType == "all")
        {
            return BadRequest(
                "SourceType must identify one source when SourceRecordId is provided."
            );
        }

        if (request.SourceRecordId.HasValue && request.BatchSize != 1)
        {
            return BadRequest("BatchSize must be 1 for an exact source-record backfill.");
        }

        var automaticUploads = new List<AutomaticFileUpload>();
        var oldUserDataFiles = new List<OldUserDataFile>();
        var remaining = request.BatchSize;

        if (sourceType is "all" or
            DocumentExtractionQueueService.AutomaticUploadSourceType)
        {
            automaticUploads = await _db.AutomaticFileUploads
                .AsNoTracking()
                .Where(upload =>
                    upload.Status == "completed" &&
                    (!request.SourceRecordId.HasValue ||
                     upload.Id == request.SourceRecordId.Value) &&
                    !_db.Documents.Any(document =>
                        document.SourceType ==
                            DocumentExtractionQueueService.AutomaticUploadSourceType &&
                        document.SourceRecordId == upload.Id &&
                        document.Versions.Any(version =>
                            version.ObjectKey == upload.ObjectKey &&
                            version.SizeBytes == upload.SizeBytes &&
                            version.SourceModifiedAtUtc == upload.LastModifiedAtUtc &&
                            (upload.B2VersionId == "" ||
                             version.B2VersionId == upload.B2VersionId) &&
                            (upload.ObjectETag == "" ||
                             version.ObjectETag == upload.ObjectETag) &&
                            (upload.Sha256 == "" || version.Sha256 == upload.Sha256) &&
                            version.ExtractionJobs.Any(job =>
                                job.PipelineVersion ==
                                    DocumentExtractionQueueService.CurrentPipelineVersion
                            )
                        )
                    )
                )
                .OrderBy(upload => upload.CreatedAtUtc)
                .ThenBy(upload => upload.Id)
                .Take(remaining)
                .ToListAsync(cancellationToken);

            remaining -= automaticUploads.Count;
        }

        if (remaining > 0 &&
            (sourceType is "all" or
             DocumentExtractionQueueService.OldUserDataSourceType))
        {
            oldUserDataFiles = await _db.OldUserDataFiles
                .AsNoTracking()
                .Where(file =>
                    file.Status == "completed" &&
                    (!request.SourceRecordId.HasValue ||
                     file.Id == request.SourceRecordId.Value) &&
                    !_db.Documents.Any(document =>
                        document.SourceType ==
                            DocumentExtractionQueueService.OldUserDataSourceType &&
                        document.SourceRecordId == file.Id &&
                        document.Versions.Any(version =>
                            version.ObjectKey == file.ObjectKey &&
                            version.SizeBytes == file.SizeBytes &&
                            version.SourceModifiedAtUtc == file.UpdatedAtUtc &&
                            (file.B2VersionId == "" ||
                             version.B2VersionId == file.B2VersionId) &&
                            (file.ObjectETag == "" ||
                             version.ObjectETag == file.ObjectETag) &&
                            (file.Sha256 == "" || version.Sha256 == file.Sha256) &&
                            version.ExtractionJobs.Any(job =>
                                job.PipelineVersion ==
                                    DocumentExtractionQueueService.CurrentPipelineVersion
                            )
                        )
                    )
                )
                .OrderBy(file => file.IndexedAtUtc)
                .ThenBy(file => file.Id)
                .Take(remaining)
                .ToListAsync(cancellationToken);
        }

        var candidateCount = automaticUploads.Count + oldUserDataFiles.Count;

        if (request.DryRun || candidateCount == 0)
        {
            return Ok(new
            {
                request.DryRun,
                sourceType,
                request.SourceRecordId,
                request.BatchSize,
                candidateCount,
                automaticUploadCount = automaticUploads.Count,
                oldUserDataFileCount = oldUserDataFiles.Count,
                mayHaveMore = candidateCount == request.BatchSize,
                documentsCreated = 0,
                versionsCreated = 0,
                jobsCreated = 0
            });
        }

        var results = new List<DocumentQueueResult>(candidateCount);
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken
        );

        foreach (var upload in automaticUploads)
        {
            results.Add(
                await _queueService.EnsureAutomaticUploadQueuedAsync(
                    upload,
                    cancellationToken
                )
            );
        }

        foreach (var file in oldUserDataFiles)
        {
            results.Add(
                await _queueService.EnsureOldUserDataFileQueuedAsync(
                    file,
                    cancellationToken
                )
            );
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            request.DryRun,
            sourceType,
            request.SourceRecordId,
            request.BatchSize,
            candidateCount,
            automaticUploadCount = automaticUploads.Count,
            oldUserDataFileCount = oldUserDataFiles.Count,
            mayHaveMore = candidateCount == request.BatchSize,
            documentsCreated = results.Count(result => result.DocumentCreated),
            versionsCreated = results.Count(result => result.VersionCreated),
            jobsCreated = results.Count(result => result.JobCreated),
            jobIds = results.Select(result => result.ExtractionJobId).ToArray()
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        var jobCounts = await _db.ExtractionJobs
            .AsNoTracking()
            .GroupBy(job => job.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .OrderBy(item => item.status)
            .ToListAsync(cancellationToken);
        var versionCounts = await _db.DocumentVersions
            .AsNoTracking()
            .GroupBy(version => version.ExtractionStatus)
            .Select(group => new { status = group.Key, count = group.Count() })
            .OrderBy(item => item.status)
            .ToListAsync(cancellationToken);
        var recentJobs = await _db.ExtractionJobs
            .AsNoTracking()
            .OrderByDescending(job => job.UpdatedAtUtc)
            .Take(20)
            .Select(job => new
            {
                job.Id,
                job.DocumentVersionId,
                job.PipelineVersion,
                job.Status,
                job.AttemptCount,
                job.MaxAttempts,
                job.ErrorCode,
                job.NextAttemptAtUtc,
                job.LeaseOwner,
                job.LeaseUntilUtc,
                job.UpdatedAtUtc,
                job.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            documents = await _db.Documents.CountAsync(cancellationToken),
            versions = await _db.DocumentVersions.CountAsync(cancellationToken),
            sections = await _db.DocumentSections.CountAsync(cancellationToken),
            derivatives = await _db.DocumentDerivatives.CountAsync(cancellationToken),
            jobCounts,
            versionCounts,
            recentJobs
        });
    }

    private IActionResult? ValidateRequest()
    {
        if (string.IsNullOrWhiteSpace(_options.AdminApiKey))
        {
            return Problem(
                "DocumentExtraction:AdminApiKey is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        var provided = Request.Headers["X-Document-Extraction-Key"].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(_options.AdminApiKey);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            return Unauthorized();
        }

        return null;
    }
}

public sealed class DocumentExtractionBackfillRequest
{
    public string SourceType { get; set; } = "all";

    /// <summary>
    /// Optional exact source row for a one-file canary. SourceType must be
    /// automatic_upload or old_user_data and BatchSize must be one.
    /// </summary>
    public Guid? SourceRecordId { get; set; }

    public int BatchSize { get; set; } = 100;

    public bool DryRun { get; set; } = true;
}
