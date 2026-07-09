using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/remote-commands")]
public class AdminRemoteCommandsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminRemoteCommandsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("request-logs")]
    public async Task<IActionResult> RequestFreshLogs(RequestFreshLogsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (dto.TakeLines <= 0)
        {
            dto.TakeLines = 500;
        }

        if (dto.TakeLines > 5000)
        {
            dto.TakeLines = 5000;
        }

        if (string.IsNullOrWhiteSpace(dto.LogType))
        {
            dto.LogType = "all";
        }

        var payload = new
        {
            logType = dto.LogType,
            takeLines = dto.TakeLines
        };

        var command = new RemoteCommand
        {
            Id = Guid.NewGuid(),
            DeviceCode = dto.DeviceCode.Trim().ToUpperInvariant(),
            CommandType = "REQUEST_LOGS",
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = "pending",
            RequestedBy = dto.RequestedBy ?? "",
            RequestedAtUtc = DateTime.UtcNow
        };

        _db.RemoteCommands.Add(command);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            command.Id,
            command.DeviceCode,
            command.CommandType,
            command.Status,
            command.PayloadJson,
            command.RequestedAtUtc
        });
    }

    [HttpPost("run-diagnostics")]
    public async Task<IActionResult> RunDiagnostics(RunDiagnosticsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var command = new RemoteCommand
        {
            Id = Guid.NewGuid(),
            DeviceCode = dto.DeviceCode.Trim().ToUpperInvariant(),
            CommandType = "RUN_DIAGNOSTICS",
            PayloadJson = "{}",
            Status = "pending",
            RequestedBy = dto.RequestedBy ?? "",
            RequestedAtUtc = DateTime.UtcNow
        };

        _db.RemoteCommands.Add(command);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            command.Id,
            command.DeviceCode,
            command.CommandType,
            command.Status,
            command.RequestedAtUtc
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetCommands(
        [FromQuery] string? deviceCode = null,
        [FromQuery] string? status = null,
        [FromQuery] int take = 100)
    {
        if (take <= 0)
        {
            take = 100;
        }

        if (take > 500)
        {
            take = 500;
        }

        var query = _db.RemoteCommands.AsQueryable();

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

        var commands = await query
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.CommandType,
                x.Status,
                x.PayloadJson,
                x.RequestedBy,
                x.RequestedAtUtc,
                x.SentAtUtc,
                x.CompletedAtUtc,
                x.ErrorMessage
            })
            .ToListAsync();

        return Ok(commands);
    }

    [HttpPost("{id:guid}/mark-failed")]
    public async Task<IActionResult> MarkCommandFailed(Guid id, MarkRemoteCommandFailedDto dto)
    {
        var command = await _db.RemoteCommands.FirstOrDefaultAsync(x => x.Id == id);

        if (command == null)
        {
            return NotFound("Remote command not found.");
        }

        command.Status = "failed";
        command.CompletedAtUtc = DateTime.UtcNow;
        command.ErrorMessage = dto.ErrorMessage ?? "";

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            command.Id,
            command.Status,
            command.ErrorMessage
        });
    }
}

public class RequestFreshLogsDto
{
    public string DeviceCode { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string LogType { get; set; } = "all";
    // all, error, file-request, upload, diagnostic

    public int TakeLines { get; set; } = 500;
}

public class RunDiagnosticsDto
{
    public string DeviceCode { get; set; } = "";

    public string RequestedBy { get; set; } = "";
}

public class MarkRemoteCommandFailedDto
{
    public string ErrorMessage { get; set; } = "";
}