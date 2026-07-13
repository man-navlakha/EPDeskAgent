using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminScanExclusionsController : ControllerBase
{
    private const string FolderPathType = "folder_path";
    private const string FolderNameType = "folder_name";
    private const string FileExtensionType = "file_extension";

    private readonly AppDbContext _db;

    public AdminScanExclusionsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("devices/{deviceCode}/scan-exclusions")]
    public async Task<IActionResult> GetDeviceScanExclusions(
        string deviceCode,
        [FromQuery] bool includeInactive = false)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var normalizedDeviceCode = NormalizeDeviceCode(deviceCode);

        var query = _db.DeviceScanExclusions
            .Where(x => x.DeviceCode == normalizedDeviceCode);

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        var exclusions = await query
            .OrderBy(x => x.ExclusionType)
            .ThenBy(x => x.Value)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.ExclusionType,
                x.Value,
                x.CreatedBy,
                x.CreatedAtUtc,
                x.IsActive
            })
            .ToListAsync();

        return Ok(new
        {
            deviceCode = normalizedDeviceCode,
            exclusions
        });
    }

    [HttpPost("devices/{deviceCode}/scan-exclusions")]
    public async Task<IActionResult> AddDeviceScanExclusion(
        string deviceCode,
        CreateDeviceScanExclusionDto dto)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (dto == null)
        {
            return BadRequest("Request body is required.");
        }

        var normalizedDeviceCode = NormalizeDeviceCode(deviceCode);
        var exclusionType = NormalizeExclusionType(dto.ExclusionType);

        if (exclusionType == "")
        {
            return BadRequest(
                "ExclusionType must be folder_path, folder_name, or file_extension."
            );
        }

        var value = NormalizeExclusionValue(exclusionType, dto.Value);

        if (value == "")
        {
            return BadRequest("Exclusion value is required.");
        }

        var valueForCompare = value.ToLowerInvariant();

        var existing = await _db.DeviceScanExclusions
            .FirstOrDefaultAsync(x =>
                x.DeviceCode == normalizedDeviceCode &&
                x.ExclusionType == exclusionType &&
                x.Value.ToLower() == valueForCompare);

        if (existing != null)
        {
            existing.IsActive = true;

            if (!string.IsNullOrWhiteSpace(dto.CreatedBy))
            {
                existing.CreatedBy = dto.CreatedBy.Trim();
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                existing.Id,
                existing.DeviceCode,
                existing.ExclusionType,
                existing.Value,
                existing.IsActive
            });
        }

        var exclusion = new DeviceScanExclusion
        {
            Id = Guid.NewGuid(),
            DeviceCode = normalizedDeviceCode,
            ExclusionType = exclusionType,
            Value = value,
            CreatedBy = dto.CreatedBy?.Trim() ?? "",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        _db.DeviceScanExclusions.Add(exclusion);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            exclusion.Id,
            exclusion.DeviceCode,
            exclusion.ExclusionType,
            exclusion.Value,
            exclusion.IsActive
        });
    }

    [HttpPatch("scan-exclusions/{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateScanExclusion(Guid id)
    {
        var exclusion = await _db.DeviceScanExclusions
            .FirstOrDefaultAsync(x => x.Id == id);

        if (exclusion == null)
        {
            return NotFound("Scan exclusion not found.");
        }

        exclusion.IsActive = false;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            exclusion.Id,
            exclusion.IsActive
        });
    }

    [HttpDelete("scan-exclusions/{id:guid}")]
    public async Task<IActionResult> DeleteScanExclusion(Guid id)
    {
        var exclusion = await _db.DeviceScanExclusions
            .FirstOrDefaultAsync(x => x.Id == id);

        if (exclusion == null)
        {
            return NotFound("Scan exclusion not found.");
        }

        _db.DeviceScanExclusions.Remove(exclusion);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            id
        });
    }

    private static string NormalizeDeviceCode(string deviceCode)
    {
        return deviceCode.Trim().ToUpperInvariant();
    }

    private static string NormalizeExclusionType(string exclusionType)
    {
        var normalized = (exclusionType ?? "")
            .Trim()
            .ToLowerInvariant()
            .Replace("-", "_");

        return normalized switch
        {
            "excludedfolders" => FolderPathType,
            "excluded_folder" => FolderPathType,
            "excluded_folders" => FolderPathType,
            "folder" => FolderPathType,
            "folder_path" => FolderPathType,
            "path" => FolderPathType,

            "excludedfoldernames" => FolderNameType,
            "excluded_folder_name" => FolderNameType,
            "excluded_folder_names" => FolderNameType,
            "folder_name" => FolderNameType,
            "name" => FolderNameType,

            "excludedfileextensions" => FileExtensionType,
            "excluded_file_extension" => FileExtensionType,
            "excluded_file_extensions" => FileExtensionType,
            "file_extension" => FileExtensionType,
            "extension" => FileExtensionType,

            _ => ""
        };
    }

    private static string NormalizeExclusionValue(string exclusionType, string value)
    {
        var normalizedValue = (value ?? "").Trim();

        if (normalizedValue == "")
        {
            return "";
        }

        if (exclusionType == FileExtensionType)
        {
            normalizedValue = normalizedValue.TrimStart('.');
            return normalizedValue == ""
                ? ""
                : "." + normalizedValue.ToLowerInvariant();
        }

        return normalizedValue.TrimEnd('\\', '/');
    }
}

public class CreateDeviceScanExclusionDto
{
    public string ExclusionType { get; set; } = "";

    public string Value { get; set; } = "";

    public string CreatedBy { get; set; } = "";
}
