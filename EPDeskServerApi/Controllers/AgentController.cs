using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly AppDbContext _db;

    public AgentController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(HeartbeatRequest request)
    {
        var device = await _db.Devices
            .FirstOrDefaultAsync(x => x.DeviceCode == request.DeviceCode);

        if (device == null)
        {
            device = new Device
            {
                Id = Guid.NewGuid(),
                DeviceCode = request.DeviceCode,
                Hostname = request.Hostname,
                Username = request.Username,
                AgentVersion = request.AgentVersion,
                Status = "online",
                LastSeenAtUtc = DateTime.UtcNow,
                RegisteredAtUtc = DateTime.UtcNow,
                IsActive = true
            };

            _db.Devices.Add(device);
        }
        else
        {
            device.Hostname = request.Hostname;
            device.Username = request.Username;
            device.AgentVersion = request.AgentVersion;
            device.Status = "online";
            device.LastSeenAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Heartbeat received"
        });
    }

    [HttpPost("files/batch")]
    public async Task<IActionResult> SyncFiles(FileBatchRequest request)
    {
        foreach (var file in request.Files)
        {
            var existing = await _db.FileIndexes.FirstOrDefaultAsync(x =>
                x.DeviceCode == request.DeviceCode &&
                x.FullPath == file.FullPath);

            if (existing == null)
            {
                existing = new FileIndex
                {
                    Id = Guid.NewGuid(),
                    DeviceCode = request.DeviceCode,
                    FullPath = file.FullPath,
                    DirectoryPath = file.DirectoryPath,
                    FileName = file.FileName,
                    Extension = file.Extension,
                    SizeBytes = file.SizeBytes,
                    CreatedAtUtc = file.CreatedAtUtc,
                    UpdatedAtUtc = file.UpdatedAtUtc,
                    LastIndexedAtUtc = DateTime.UtcNow,
                    IsDeleted = file.IsDeleted
                };

                _db.FileIndexes.Add(existing);
            }
            else
            {
                existing.DirectoryPath = file.DirectoryPath;
                existing.FileName = file.FileName;
                existing.Extension = file.Extension;
                existing.SizeBytes = file.SizeBytes;
                existing.CreatedAtUtc = file.CreatedAtUtc;
                existing.UpdatedAtUtc = file.UpdatedAtUtc;
                existing.LastIndexedAtUtc = DateTime.UtcNow;
                existing.IsDeleted = file.IsDeleted;
            }
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            count = request.Files.Count
        });
    }

    [HttpGet("commands")]
    public async Task<IActionResult> GetCommands([FromQuery] string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var requests = await _db.FileRequests
            .Where(x => x.DeviceCode == deviceCode && x.Status == "pending")
            .OrderBy(x => x.RequestedAtUtc)
            .Take(5)
            .ToListAsync();

        foreach (var request in requests)
        {
            request.Status = "sent_to_agent";
            request.StartedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var commands = requests.Select(x => new
        {
            type = "UPLOAD_FILE",
            requestId = x.Id,
            filePath = x.RequestedPath
        });

        return Ok(commands);
    }

    [HttpPost("file-request/{requestId:guid}/upload")]
    public async Task<IActionResult> UploadRequestedFile(Guid requestId, IFormFile file)
    {
        var request = await _db.FileRequests.FirstOrDefaultAsync(x => x.Id == requestId);

        if (request == null)
        {
            return NotFound("File request not found.");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("Uploaded file is empty.");
        }

        request.Status = "uploading";
        await _db.SaveChangesAsync();

        var uploadRoot = Path.Combine(AppContext.BaseDirectory, "UploadedFiles");
        Directory.CreateDirectory(uploadRoot);

        var requestFolder = Path.Combine(uploadRoot, request.Id.ToString("N"));
        Directory.CreateDirectory(requestFolder);

        var safeFileName = Path.GetFileName(file.FileName);
        var serverFilePath = Path.Combine(requestFolder, safeFileName);

        await using (var stream = new FileStream(serverFilePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        request.Status = "completed";
        request.CompletedAtUtc = DateTime.UtcNow;
        request.ServerFilePath = serverFilePath;
        request.OriginalFileName = safeFileName;
        request.ErrorMessage = null;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "File uploaded successfully",
            requestId = request.Id
        });
    }

    [HttpPost("file-request/{requestId:guid}/fail")]
    public async Task<IActionResult> MarkFileRequestFailed(Guid requestId, FailFileRequestDto dto)
    {
        var request = await _db.FileRequests.FirstOrDefaultAsync(x => x.Id == requestId);

        if (request == null)
        {
            return NotFound("File request not found.");
        }

        request.Status = "failed";
        request.ErrorMessage = dto.ErrorMessage;
        request.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "File request marked as failed"
        });
    }
}

public class HeartbeatRequest
{
    public string DeviceCode { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string AgentVersion { get; set; } = "";
}

public class FileBatchRequest
{
    public string DeviceCode { get; set; } = "";
    public List<FileSyncItem> Files { get; set; } = new();
}

public class FileSyncItem
{
    public string FullPath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

public class FailFileRequestDto
{
    public string ErrorMessage { get; set; } = "";
}