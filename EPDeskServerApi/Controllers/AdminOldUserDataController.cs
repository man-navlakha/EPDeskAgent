using System.Security.Cryptography;
using System.Text;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/old-user-data")]
public sealed class AdminOldUserDataController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly OldUserDataImportOptions _options;
    private readonly B2StorageOptions _storageOptions;

    public AdminOldUserDataController(
        AppDbContext db,
        IObjectStorageService storage,
        IOptions<OldUserDataImportOptions> options,
        IOptions<B2StorageOptions> storageOptions)
    {
        _db = db;
        _storage = storage;
        _options = options.Value;
        _storageOptions = storageOptions.Value;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            return Problem(
                "OldUserDataImport:RootPath is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        var rootPath = Path.GetFullPath(_options.RootPath.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(rootPath))
        {
            return Problem(
                $"The configured import root is not accessible to this API host: {rootPath}",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        var rootIdentity = CreateRootIdentity(rootPath);
        var existing = await _db.OldUserDataImportJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RootPathIdentity == rootIdentity, cancellationToken);

        if (existing != null)
        {
            return Ok(ToJobResponse(existing, alreadyExists: true));
        }

        var job = new Models.OldUserDataImportJob
        {
            Id = Guid.NewGuid(),
            RootPath = rootPath,
            RootPathIdentity = rootIdentity,
            SourceLabel = string.IsNullOrWhiteSpace(_options.SourceLabel)
                ? "Old User Data"
                : _options.SourceLabel.Trim(),
            Status = "pending",
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
            _db.Entry(job).State = EntityState.Detached;
            existing = await _db.OldUserDataImportJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.RootPathIdentity == rootIdentity,
                    cancellationToken
                );

            if (existing != null)
            {
                return Ok(ToJobResponse(existing, alreadyExists: true));
            }

            throw;
        }

        return Accepted(ToJobResponse(job, alreadyExists: false));
    }

    [HttpGet]
    public async Task<IActionResult> GetJobs(CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var jobs = await _db.OldUserDataImportJobs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.SourceLabel,
                x.Status,
                x.IndexedFileCount,
                x.IndexedSizeBytes,
                x.UploadedFileCount,
                x.UploadedSizeBytes,
                x.FailedFileCount,
                x.ErrorMessage,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.ScanCompletedAtUtc,
                x.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(jobs);
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetJob(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var job = await _db.OldUserDataImportJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        return job == null ? NotFound() : Ok(ToJobResponse(job, alreadyExists: true));
    }

    [HttpGet("{jobId:guid}/users")]
    public async Task<IActionResult> GetUsers(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var users = await _db.OldUserDataFiles
            .AsNoTracking()
            .Where(x => x.ImportJobId == jobId)
            .GroupBy(x => new { x.UserFolder, x.DeviceCode })
            .Select(group => new
            {
                group.Key.UserFolder,
                group.Key.DeviceCode,
                IndexedFileCount = group.LongCount(),
                IndexedSizeBytes = group.Sum(x => x.SizeBytes),
                UploadedFileCount = group.LongCount(x => x.Status == "completed"),
                UploadedSizeBytes = group
                    .Where(x => x.Status == "completed")
                    .Sum(x => x.SizeBytes),
                FailedFileCount = group.LongCount(x =>
                    x.Status == "failed" || x.Status == "missing"),
                SkippedFileCount = group.LongCount(x => x.Status == "skipped")
            })
            .OrderBy(x => x.UserFolder)
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    [HttpGet("{jobId:guid}/files")]
    public async Task<IActionResult> GetFiles(
        Guid jobId,
        [FromQuery] string? userFolder = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 1000);

        var query = _db.OldUserDataFiles
            .AsNoTracking()
            .Where(x => x.ImportJobId == jobId);

        if (!string.IsNullOrWhiteSpace(userFolder))
        {
            var value = userFolder.Trim();
            query = query.Where(x => x.UserFolder == value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var value = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim().ToLower();
            query = query.Where(x =>
                x.FileName.ToLower().Contains(value) ||
                x.RelativePath.ToLower().Contains(value));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var files = await query
            .OrderBy(x => x.UserFolder)
            .ThenBy(x => x.RelativePath)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.UserFolder,
                x.DeviceCode,
                x.RelativePath,
                x.FileName,
                x.Extension,
                x.SizeBytes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.Status,
                x.UploadedBytes,
                x.AttemptCount,
                x.ErrorMessage,
                x.IndexedAtUtc,
                x.UploadStartedAtUtc,
                x.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, skip, take, files });
    }

    [HttpPost("{jobId:guid}/pause")]
    public async Task<IActionResult> Pause(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var updated = await _db.OldUserDataImportJobs
            .Where(x => x.Id == jobId &&
                        x.Status != "completed" &&
                        x.Status != "completed_with_errors")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "paused")
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);

        return updated == 0 ? NotFound() : Ok(new { success = true, jobId, status = "paused" });
    }

    [HttpPost("{jobId:guid}/resume")]
    public async Task<IActionResult> Resume(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var job = await _db.OldUserDataImportJobs
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        if (job == null)
        {
            return NotFound();
        }

        if (job.Status == "completed")
        {
            return Conflict("The one-time import is already complete.");
        }

        if (job.ScanCompletedAtUtc == null)
        {
            job.Status = "pending";
        }
        else
        {
            await _db.OldUserDataFiles
                .Where(x => x.ImportJobId == jobId &&
                            (x.Status == "failed" || x.Status == "missing"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "indexed")
                    .SetProperty(x => x.ErrorMessage, ""),
                    cancellationToken);

            job.Status = "uploading";
            job.FailedFileCount = 0;
        }

        job.ErrorMessage = "";
        job.CompletedAtUtc = null;
        job.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Accepted(new { success = true, jobId, job.Status });
    }

    [HttpGet("files/{fileId:guid}/download")]
    public async Task<IActionResult> GetDownloadUrl(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var authorizationError = ValidateRequest();
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var file = await _db.OldUserDataFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == fileId, cancellationToken);

        if (file == null)
        {
            return NotFound();
        }

        if (file.Status != "completed")
        {
            return Conflict($"File is not ready. Current status: {file.Status}");
        }

        var validFor = TimeSpan.FromMinutes(_storageOptions.DownloadUrlMinutes);
        var url = await _storage.CreateDownloadUrlAsync(
            file.ObjectKey,
            validFor,
            cancellationToken
        );

        return Ok(new
        {
            file.Id,
            file.FileName,
            file.RelativePath,
            downloadUrl = url,
            expiresAtUtc = DateTime.UtcNow.Add(validFor)
        });
    }

    private IActionResult? ValidateRequest()
    {
        if (!_options.Enabled)
        {
            return Problem(
                "Old User Data import is disabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Problem(
                "OldUserDataImport:ApiKey is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }

        var provided = Request.Headers["X-Import-Key"].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(_options.ApiKey);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            return Unauthorized();
        }

        return null;
    }

    private static string CreateRootIdentity(string rootPath)
    {
        return OperatingSystem.IsWindows()
            ? rootPath.ToUpperInvariant()
            : rootPath;
    }

    private static object ToJobResponse(
        Models.OldUserDataImportJob job,
        bool alreadyExists)
    {
        return new
        {
            job.Id,
            job.SourceLabel,
            job.Status,
            job.IndexedFileCount,
            job.IndexedSizeBytes,
            job.UploadedFileCount,
            job.UploadedSizeBytes,
            job.FailedFileCount,
            job.ErrorMessage,
            job.CreatedAtUtc,
            job.UpdatedAtUtc,
            job.ScanCompletedAtUtc,
            job.CompletedAtUtc,
            alreadyExists
        };
    }
}
