using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices([FromQuery] string? updateStatus = null)
    {
        var now = DateTime.UtcNow;
        var latestVersion = await GetLatestAgentVersionAsync();

        var deviceRows = await _db.Devices
            .OrderBy(x => x.DeviceCode)
            .Select(x => new
            {
                x.DeviceCode,
                x.Nickname,
                x.Hostname,
                x.Username,
                x.AgentVersion,
                x.LastSeenAtUtc
            })
            .ToListAsync();

        var devices = deviceRows
            .Select(x =>
            {
                var deviceUpdateStatus = GetUpdateStatus(latestVersion, x.AgentVersion);

                return new
                {
                    x.DeviceCode,
                    x.Nickname,
                    DisplayName = x.Nickname == "" ? x.DeviceCode : x.Nickname,
                    x.Hostname,
                    x.Username,
                    x.AgentVersion,
                    LatestVersion = latestVersion,
                    UpdateStatus = deviceUpdateStatus,
                    UpdateAvailable = deviceUpdateStatus == "outdated",
                    Status = x.LastSeenAtUtc != null &&
                             x.LastSeenAtUtc > now.AddMinutes(-2)
                        ? "online"
                        : "offline",
                    x.LastSeenAtUtc
                };
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(updateStatus))
        {
            var normalizedUpdateStatus = updateStatus.Trim().ToLowerInvariant();

            devices = devices
                .Where(x => x.UpdateStatus == normalizedUpdateStatus)
                .ToList();
        }

        return Ok(devices);
    }

    [HttpGet("devices/version-summary")]
    public async Task<IActionResult> GetDeviceVersionSummary()
    {
        var now = DateTime.UtcNow;
        var latestVersion = await GetLatestAgentVersionAsync();

        var deviceRows = await _db.Devices
            .Select(x => new
            {
                x.AgentVersion,
                x.LastSeenAtUtc
            })
            .ToListAsync();

        var updateStatuses = deviceRows
            .Select(x => GetUpdateStatus(latestVersion, x.AgentVersion))
            .ToList();

        var versionGroups = deviceRows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.AgentVersion)
                ? "unknown"
                : x.AgentVersion.Trim())
            .Select(x => new
            {
                version = x.Key,
                count = x.Count()
            })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.version)
            .ToList();

        return Ok(new
        {
            latestVersion,
            totalDevices = deviceRows.Count,
            onlineDevices = deviceRows.Count(x =>
                x.LastSeenAtUtc != null &&
                x.LastSeenAtUtc > now.AddMinutes(-2)),
            latestDevices = updateStatuses.Count(x => x == "latest"),
            outdatedDevices = updateStatuses.Count(x => x == "outdated"),
            unknownVersionDevices = updateStatuses.Count(x => x == "unknown"),
            newerThanLatestDevices = updateStatuses.Count(x => x == "newer"),
            versionGroups
        });
    }

    [HttpPatch("devices/{deviceCode}/nickname")]
    public async Task<IActionResult> UpdateDeviceNickname(
        string deviceCode,
        UpdateDeviceNicknameDto dto)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (dto == null)
        {
            return BadRequest("Request body is required.");
        }

        var normalizedDeviceCode = deviceCode.Trim().ToUpperInvariant();
        var nickname = dto.Nickname?.Trim() ?? "";

        if (nickname.Length > 100)
        {
            return BadRequest("Nickname cannot be longer than 100 characters.");
        }

        var device = await _db.Devices
            .FirstOrDefaultAsync(x => x.DeviceCode.ToUpper() == normalizedDeviceCode);

        if (device == null)
        {
            return NotFound("Device not found.");
        }

        device.Nickname = nickname;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            device.DeviceCode,
            device.Nickname,
            DisplayName = device.Nickname == "" ? device.DeviceCode : device.Nickname
        });
    }

    [HttpGet("files/search")]
    public async Task<IActionResult> SearchFiles([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Search query is required.");
        }

        var results = await _db.FileIndexes
            .Where(x => x.FileName.Contains(query) || x.FullPath.Contains(query))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(100)
            .Select(x => new
            {
                x.DeviceCode,
                x.FileName,
                x.FullPath,
                x.DirectoryPath,
                x.Extension,
                x.SizeBytes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc
            })
            .ToListAsync();

        return Ok(results);
    }

    [HttpPost("file-requests")]
    public async Task<IActionResult> CreateFileRequest(CreateFileRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.RequestedPath))
        {
            return BadRequest("Requested file path is required.");
        }

        var request = new FileRequest
        {
            Id = Guid.NewGuid(),
            DeviceCode = dto.DeviceCode,
            RequestedPath = dto.RequestedPath,
            RequestedBy = dto.RequestedBy,
            Reason = dto.Reason,
            Status = "pending",
            RequestedAtUtc = DateTime.UtcNow
        };

        _db.FileRequests.Add(request);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            requestId = request.Id,
            status = request.Status
        });
    }

    [HttpGet("file-requests")]
    public async Task<IActionResult> GetFileRequests()
    {
        var requests = await _db.FileRequests
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.RequestedPath,
                x.RequestedBy,
                x.Reason,
                x.Status,
                x.RequestedAtUtc,
                x.StartedAtUtc,
                x.CompletedAtUtc,
                x.OriginalFileName,
                x.ErrorMessage
            })
            .ToListAsync();

        return Ok(requests);
    }

    [HttpGet("devices/{deviceCode}/files")]
    public async Task<IActionResult> GetDeviceFiles(
    string deviceCode,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 100,
    [FromQuery] string? query = null,
    [FromQuery] string? extension = null,
    [FromQuery] bool includeDeleted = false)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (page <= 0)
        {
            page = 1;
        }

        if (pageSize <= 0)
        {
            pageSize = 100;
        }

        if (pageSize > 500)
        {
            pageSize = 500;
        }

        var normalizedDeviceCode = deviceCode.Trim().ToUpper();

        var filesQuery = _db.FileIndexes
            .Where(x => x.DeviceCode.ToUpper() == normalizedDeviceCode);

        if (!includeDeleted)
        {
            filesQuery = filesQuery.Where(x => !x.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = query.Trim().ToLower();

            filesQuery = filesQuery.Where(x =>
                x.FileName.ToLower().Contains(search) ||
                x.FullPath.ToLower().Contains(search) ||
                x.DirectoryPath.ToLower().Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            var ext = extension.Trim();

            if (!ext.StartsWith("."))
            {
                ext = "." + ext;
            }

            filesQuery = filesQuery.Where(x => x.Extension.ToLower() == ext.ToLower());
        }

        var totalFiles = await filesQuery.CountAsync();

        var files = await filesQuery
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.FileName,
                x.FullPath,
                x.DirectoryPath,
                x.Extension,
                x.SizeBytes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.LastIndexedAtUtc,
                x.IsDeleted
            })
            .ToListAsync();

        return Ok(new
        {
            deviceCode = normalizedDeviceCode,
            page,
            pageSize,
            totalFiles,
            totalPages = (int)Math.Ceiling(totalFiles / (double)pageSize),
            files
        });
    }

    [HttpPost("file-requests/zip-files")]
    public async Task<IActionResult> CreateMultipleFilesZipRequest(CreateZipFilesRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (dto.RequestedPaths == null || dto.RequestedPaths.Count == 0)
        {
            return BadRequest("At least one file path is required.");
        }

        var cleanedPaths = dto.RequestedPaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .ToList();

        if (cleanedPaths.Count == 0)
        {
            return BadRequest("Valid file paths are required.");
        }

        if (cleanedPaths.Count > 200)
        {
            return BadRequest("Maximum 200 files can be requested in one ZIP.");
        }

        var request = new FileRequest
        {
            Id = Guid.NewGuid(),
            DeviceCode = dto.DeviceCode.Trim().ToUpperInvariant(),
            RequestedPath = "",
            RequestedPathsJson = JsonSerializer.Serialize(cleanedPaths),
            RequestedBy = dto.RequestedBy,
            Reason = dto.Reason,
            Status = "pending",
            RequestType = "multiple_files_zip",
            RequestedAtUtc = DateTime.UtcNow,
            OriginalFileName = $"multiple-files-{DateTime.UtcNow:yyyyMMddHHmmss}.zip"
        };

        _db.FileRequests.Add(request);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            requestId = request.Id,
            status = request.Status,
            requestType = request.RequestType
        });
    }

    [HttpPost("file-requests/folder-zip")]
    public async Task<IActionResult> CreateFolderZipRequest(CreateFolderZipRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.FolderPath))
        {
            return BadRequest("Folder path is required.");
        }

        var folderPath = dto.FolderPath.Trim();

        var request = new FileRequest
        {
            Id = Guid.NewGuid(),
            DeviceCode = dto.DeviceCode.Trim().ToUpperInvariant(),
            RequestedPath = folderPath,
            RequestedPathsJson = "",
            RequestedBy = dto.RequestedBy,
            Reason = dto.Reason,
            Status = "pending",
            RequestType = "folder_zip",
            RequestedAtUtc = DateTime.UtcNow,
            OriginalFileName = $"folder-{DateTime.UtcNow:yyyyMMddHHmmss}.zip"
        };

        _db.FileRequests.Add(request);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            requestId = request.Id,
            status = request.Status,
            requestType = request.RequestType
        });
    }

    [HttpGet("file-requests/{id:guid}/download")]
    public async Task<IActionResult> DownloadFile(Guid id)
    {
        var request = await _db.FileRequests.FirstOrDefaultAsync(x => x.Id == id);

        if (request == null)
        {
            return NotFound("File request not found.");
        }

        if (request.Status != "completed")
        {
            return BadRequest($"File is not ready. Current status: {request.Status}");
        }

        if (string.IsNullOrWhiteSpace(request.ServerFilePath))
        {
            return BadRequest("Server file path is empty.");
        }

        if (!System.IO.File.Exists(request.ServerFilePath))
        {
            return NotFound("Uploaded file not found on server.");
        }

        var downloadFileName = request.OriginalFileName ?? Path.GetFileName(request.ServerFilePath);

        return PhysicalFile(
            request.ServerFilePath,
            "application/octet-stream",
            downloadFileName
        );
    }

    private async Task<string> GetLatestAgentVersionAsync()
    {
        return await _db.AgentVersions
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => x.Version)
            .FirstOrDefaultAsync() ?? "";
    }

    private static string GetUpdateStatus(string latestVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion) ||
            string.IsNullOrWhiteSpace(currentVersion))
        {
            return "unknown";
        }

        if (!Version.TryParse(latestVersion.Trim(), out var latest) ||
            !Version.TryParse(currentVersion.Trim(), out var current))
        {
            return "unknown";
        }

        if (current == latest)
        {
            return "latest";
        }

        return current < latest ? "outdated" : "newer";
    }
}

public class CreateFileRequestDto
{
    public string DeviceCode { get; set; } = "";
    public string RequestedPath { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class UpdateDeviceNicknameDto
{
    public string? Nickname { get; set; }
}

public class CreateZipFilesRequestDto
{
    public string DeviceCode { get; set; } = "";

    public List<string> RequestedPaths { get; set; } = new();

    public string RequestedBy { get; set; } = "";

    public string Reason { get; set; } = "";
}

public class CreateFolderZipRequestDto
{
    public string DeviceCode { get; set; } = "";

    public string FolderPath { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string Reason { get; set; } = "";
}
