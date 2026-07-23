using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/file-uploads")]
public sealed class AdminFileUploadsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IObjectStorageService _storageService;
    private readonly B2StorageOptions _storageOptions;

    public AdminFileUploadsController(
        AppDbContext db,
        IObjectStorageService storageService,
        IOptions<B2StorageOptions> storageOptions)
    {
        _db = db;
        _storageService = storageService;
        _storageOptions = storageOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> GetUploads(
        [FromQuery] string? deviceCode = null,
        [FromQuery] string? status = null,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);

        var query = _db.AutomaticFileUploads.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(deviceCode))
        {
            var normalizedDeviceCode = deviceCode.Trim().ToUpperInvariant();
            query = query.Where(x => x.DeviceCode == normalizedDeviceCode);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == normalizedStatus);
        }

        var uploads = await query
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.FullPath,
                x.FileName,
                x.Extension,
                x.SizeBytes,
                x.LastModifiedAtUtc,
                x.Status,
                x.ErrorMessage,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(uploads);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> GetDownloadUrl(
        Guid id,
        CancellationToken cancellationToken)
    {
        var upload = await _db.AutomaticFileUploads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (upload == null)
        {
            return NotFound("File upload not found.");
        }

        if (upload.Status != "completed")
        {
            return BadRequest($"File is not ready. Current status: {upload.Status}");
        }

        if (!await _storageService.ObjectExistsAsync(upload.ObjectKey, cancellationToken))
        {
            return NotFound("Backblaze object not found.");
        }

        var validFor = TimeSpan.FromMinutes(_storageOptions.DownloadUrlMinutes);
        var url = await _storageService.CreateDownloadUrlAsync(
            upload.ObjectKey,
            validFor,
            cancellationToken
        );

        return Ok(new
        {
            uploadId = upload.Id,
            upload.FileName,
            downloadUrl = url,
            expiresAtUtc = DateTime.UtcNow.Add(validFor)
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteUpload(
        Guid id,
        CancellationToken cancellationToken)
    {
        var upload = await _db.AutomaticFileUploads
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (upload == null)
        {
            return NotFound("File upload not found.");
        }

        if (upload.Status == "deleted")
        {
            return Ok(new
            {
                success = true,
                uploadId = upload.Id,
                upload.Status
            });
        }

        if (upload.Status != "completed")
        {
            return BadRequest(
                $"Only completed files can be deleted. Current status: {upload.Status}"
            );
        }

        await _storageService.DeleteObjectAsync(
            upload.ObjectKey,
            cancellationToken
        );

        upload.Status = "deleted";
        upload.MultipartUploadId = "";
        upload.ErrorMessage = "";
        upload.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            uploadId = upload.Id,
            upload.Status
        });
    }
}
