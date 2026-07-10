using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
public class AgentLogsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AgentLogsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("api/agent/logs")]
    public async Task<IActionResult> UploadLogs(UploadAgentLogsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (dto.Logs == null || dto.Logs.Count == 0)
        {
            return BadRequest("Logs are required.");
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();

        var logs = dto.Logs.Select(x => new AgentLog
        {
            Id = Guid.NewGuid(),
            DeviceCode = deviceCode,
            RequestId = x.RequestId,
            Level = string.IsNullOrWhiteSpace(x.Level) ? "INFO" : x.Level.Trim().ToUpperInvariant(),
            Category = x.Category ?? "",
            Message = x.Message ?? "",
            Step = x.Step ?? "",
            DetailsJson = x.DetailsJson ?? "",
            CreatedAtUtc = x.CreatedAtUtc == default ? DateTime.UtcNow : ForceUtc(x.CreatedAtUtc)
        }).ToList();

        _db.AgentLogs.AddRange(logs);
        if (dto.CommandId.HasValue)
        {
            var command = await _db.RemoteCommands
                .FirstOrDefaultAsync(x => x.Id == dto.CommandId.Value);

            if (command != null)
            {
                command.Status = "completed";
                command.CompletedAtUtc = DateTime.UtcNow;
                command.ErrorMessage = "";
            }
        }
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            count = logs.Count
        });
    }

    [HttpGet("api/admin/devices/{deviceCode}/logs")]
    public async Task<IActionResult> GetDeviceLogs(
        string deviceCode,
        [FromQuery] string? level = null,
        [FromQuery] int take = 200)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (take <= 0)
        {
            take = 200;
        }

        if (take > 1000)
        {
            take = 1000;
        }

        var normalizedDeviceCode = deviceCode.Trim().ToUpperInvariant();

        var query = _db.AgentLogs
            .Where(x => x.DeviceCode == normalizedDeviceCode);

        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalizedLevel = level.Trim().ToUpperInvariant();
            query = query.Where(x => x.Level == normalizedLevel);
        }

        var logs = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.RequestId,
                x.Level,
                x.Category,
                x.Step,
                x.Message,
                x.DetailsJson,
                x.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("api/admin/file-requests/{requestId:guid}/logs")]
    public async Task<IActionResult> GetRequestLogs(Guid requestId)
    {
        var logs = await _db.AgentLogs
            .Where(x => x.RequestId == requestId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.RequestId,
                x.Level,
                x.Category,
                x.Step,
                x.Message,
                x.DetailsJson,
                x.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(logs);
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
}

public class UploadAgentLogsDto
{
    public string DeviceCode { get; set; } = "";
    public Guid? CommandId { get; set; }
    public List<AgentLogItemDto> Logs { get; set; } = new();
}

public class AgentLogItemDto
{
    public Guid? RequestId { get; set; }

    public string Level { get; set; } = "INFO";

    public string Category { get; set; } = "";

    public string Step { get; set; } = "";

    public string Message { get; set; } = "";

    public string DetailsJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}