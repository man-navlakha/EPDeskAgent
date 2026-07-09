using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
        try
        {
            if (request == null)
            {
                return BadRequest("Request body is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.DeviceCode))
            {
                return BadRequest("Device code is required.");
            }

            if (request.Files == null || request.Files.Count == 0)
            {
                return Ok(new
                {
                    success = true,
                    count = 0,
                    message = "No files received."
                });
            }

            foreach (var file in request.Files)
            {
                if (string.IsNullOrWhiteSpace(file.FullPath))
                {
                    continue;
                }

                var existing = await _db.FileIndexes.FirstOrDefaultAsync(x =>
                    x.DeviceCode == request.DeviceCode &&
                    x.FullPath == file.FullPath);

                var createdAtUtc = ForceUtc(file.CreatedAtUtc);
                var updatedAtUtc = ForceUtc(file.UpdatedAtUtc);

                if (existing == null)
                {
                    existing = new FileIndex
                    {
                        Id = Guid.NewGuid(),
                        DeviceCode = request.DeviceCode,
                        FullPath = file.FullPath,
                        DirectoryPath = file.DirectoryPath ?? "",
                        FileName = file.FileName ?? "",
                        Extension = file.Extension ?? "",
                        SizeBytes = file.SizeBytes,
                        CreatedAtUtc = createdAtUtc,
                        UpdatedAtUtc = updatedAtUtc,
                        LastIndexedAtUtc = DateTime.UtcNow,
                        IsDeleted = file.IsDeleted
                    };

                    _db.FileIndexes.Add(existing);
                }
                else
                {
                    existing.DirectoryPath = file.DirectoryPath ?? "";
                    existing.FileName = file.FileName ?? "";
                    existing.Extension = file.Extension ?? "";
                    existing.SizeBytes = file.SizeBytes;
                    existing.CreatedAtUtc = createdAtUtc;
                    existing.UpdatedAtUtc = updatedAtUtc;
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
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message,
                inner = ex.InnerException?.Message,
                stack = ex.StackTrace
            });
        }
    }

    private static DateTime ForceUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Local)
        {
            return value.ToUniversalTime();
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    [HttpGet("commands")]
    public async Task<IActionResult> GetCommands([FromQuery] string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var normalizedDeviceCode = deviceCode.Trim().ToUpperInvariant();

        var commands = new List<object>();

        var requests = await _db.FileRequests
            .Where(x => x.DeviceCode == normalizedDeviceCode && x.Status == "pending")
            .OrderBy(x => x.RequestedAtUtc)
            .Take(5)
            .ToListAsync();

        foreach (var request in requests)
        {
            request.Status = "sent_to_agent";
            request.StartedAtUtc = DateTime.UtcNow;

            if (request.RequestType == "multiple_files_zip")
            {
                var paths = new List<string>();

                if (!string.IsNullOrWhiteSpace(request.RequestedPathsJson))
                {
                    paths = JsonSerializer.Deserialize<List<string>>(request.RequestedPathsJson)
                        ?? new List<string>();
                }

                commands.Add(new
                {
                    type = "UPLOAD_ZIP",
                    requestType = "multiple_files_zip",
                    requestId = request.Id,
                    paths
                });
            }
            else if (request.RequestType == "folder_zip")
            {
                commands.Add(new
                {
                    type = "UPLOAD_ZIP",
                    requestType = "folder_zip",
                    requestId = request.Id,
                    folderPath = request.RequestedPath
                });
            }
            else
            {
                commands.Add(new
                {
                    type = "UPLOAD_FILE",
                    requestType = "single_file",
                    requestId = request.Id,
                    filePath = request.RequestedPath
                });
            }
        }

        var remoteCommands = await _db.RemoteCommands
            .Where(x => x.DeviceCode == normalizedDeviceCode && x.Status == "pending")
            .OrderBy(x => x.RequestedAtUtc)
            .Take(5)
            .ToListAsync();

        foreach (var remoteCommand in remoteCommands)
        {
            remoteCommand.Status = "sent_to_agent";
            remoteCommand.SentAtUtc = DateTime.UtcNow;

            commands.Add(new
            {
                type = remoteCommand.CommandType,
                commandId = remoteCommand.Id,
                payloadJson = remoteCommand.PayloadJson
            });
        }

        await _db.SaveChangesAsync();

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