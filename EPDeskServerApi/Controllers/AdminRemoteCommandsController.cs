using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/remote-commands")]
public class AdminRemoteCommandsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public AdminRemoteCommandsController(
        AppDbContext db,
        IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
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

    [HttpPost("scan/start")]
    public Task<IActionResult> StartScan(ActivityControlCommandDto dto)
    {
        return QueueActivityControlCommandAsync(
            dto,
            "START_SCAN",
            "Scan start command queued."
        );
    }

    [HttpPost("scan/stop")]
    public Task<IActionResult> StopScan(ActivityControlCommandDto dto)
    {
        return QueueActivityControlCommandAsync(
            dto,
            "STOP_SCAN",
            "Scan stop command queued."
        );
    }

    [HttpPost("file-upload/start")]
    public Task<IActionResult> StartFileUpload(ActivityControlCommandDto dto)
    {
        return QueueActivityControlCommandAsync(
            dto,
            "START_FILE_UPLOAD",
            "Automatic file upload start command queued."
        );
    }

    [HttpPost("file-upload/stop")]
    public Task<IActionResult> StopFileUpload(ActivityControlCommandDto dto)
    {
        return QueueActivityControlCommandAsync(
            dto,
            "STOP_FILE_UPLOAD",
            "Automatic file upload stop command queued."
        );
    }

    private async Task<IActionResult> QueueActivityControlCommandAsync(
        ActivityControlCommandDto dto,
        string commandType,
        string message)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();

        if (!await _db.Devices.AnyAsync(x => x.DeviceCode == deviceCode))
        {
            return NotFound("Device not found.");
        }

        var activeCommand = await _db.RemoteCommands
            .Where(x =>
                x.DeviceCode == deviceCode &&
                x.CommandType == commandType &&
                (x.Status == "pending" || x.Status == "sent_to_agent"))
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync();

        if (activeCommand != null)
        {
            return Accepted(new
            {
                success = true,
                message = "An equivalent command is already active.",
                command = ToActivityCommandResponse(activeCommand)
            });
        }

        var command = new RemoteCommand
        {
            Id = Guid.NewGuid(),
            DeviceCode = deviceCode,
            CommandType = commandType,
            PayloadJson = "{}",
            Status = "pending",
            RequestedBy = dto.RequestedBy ?? "",
            RequestedAtUtc = DateTime.UtcNow
        };

        _db.RemoteCommands.Add(command);
        await _db.SaveChangesAsync();

        return Accepted(new
        {
            success = true,
            message,
            command = ToActivityCommandResponse(command)
        });
    }

    private static object ToActivityCommandResponse(RemoteCommand command)
    {
        return new
        {
            command.Id,
            command.DeviceCode,
            command.CommandType,
            command.Status,
            command.RequestedBy,
            command.RequestedAtUtc
        };
    }

    [HttpPost("remove-epdesk-agent")]
    public async Task<IActionResult> RemoveEpDeskAgent(RemoveEpDeskAgentDto dto)
    {
        var configuredRemovalKey =
            _configuration["Security:AgentRemovalApiKey"];

        if (string.IsNullOrWhiteSpace(configuredRemovalKey))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                "Agent removal is disabled until Security:AgentRemovalApiKey is configured."
            );
        }

        var suppliedRemovalKey =
            Request.Headers["X-Agent-Removal-Key"].ToString();

        if (!KeysMatch(configuredRemovalKey, suppliedRemovalKey))
        {
            return Unauthorized(
                "A valid X-Agent-Removal-Key header is required."
            );
        }

        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (!string.Equals(
                dto.Confirmation,
                "REMOVE EPDesk Agent",
                StringComparison.Ordinal))
        {
            return BadRequest(
                "Confirmation must be exactly 'REMOVE EPDesk Agent'."
            );
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();
        var deviceExists = await _db.Devices.AnyAsync(
            x => x.DeviceCode == deviceCode
        );

        if (!deviceExists)
        {
            return NotFound("Device not found.");
        }

        var existingCommand = await _db.RemoteCommands
            .Where(x =>
                x.DeviceCode == deviceCode &&
                x.CommandType == "REMOVE_EPDESK_AGENT" &&
                (x.Status == "pending" || x.Status == "sent_to_agent"))
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync();

        if (existingCommand != null)
        {
            return Conflict(new
            {
                success = false,
                message = "An Agent removal command is already active for this device.",
                existingCommand.Id,
                existingCommand.Status
            });
        }

        var command = new RemoteCommand
        {
            Id = Guid.NewGuid(),
            DeviceCode = deviceCode,
            CommandType = "REMOVE_EPDESK_AGENT",
            PayloadJson = "{}",
            Status = "pending",
            RequestedBy = dto.RequestedBy ?? "",
            RequestedAtUtc = DateTime.UtcNow
        };

        _db.RemoteCommands.Add(command);
        await _db.SaveChangesAsync();

        return Accepted(new
        {
            success = true,
            message =
                "Removal queued. The device will uninstall every MSI registered with the exact display name 'EPDesk Agent'.",
            command.Id,
            command.DeviceCode,
            command.CommandType,
            command.Status,
            command.RequestedAtUtc
        });
    }

    private static bool KeysMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);

        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expectedBytes,
                   suppliedBytes
               );
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

public class RemoveEpDeskAgentDto
{
    public string DeviceCode { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string Confirmation { get; set; } = "";
}

public class ActivityControlCommandDto
{
    public string DeviceCode { get; set; } = "";

    public string RequestedBy { get; set; } = "";
}
