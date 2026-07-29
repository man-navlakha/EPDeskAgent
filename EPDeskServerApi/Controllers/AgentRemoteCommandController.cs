using EPDeskServerApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/agent/remote-command")]
public class AgentRemoteCommandController : ControllerBase
{
    private readonly AppDbContext _db;

    public AgentRemoteCommandController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("{commandId:guid}/fail")]
    public async Task<IActionResult> MarkRemoteCommandFailed(
        Guid commandId,
        AgentRemoteCommandFailDto dto)
    {
        var command = await _db.RemoteCommands
            .FirstOrDefaultAsync(x => x.Id == commandId);

        if (command == null)
        {
            return NotFound("Remote command not found.");
        }

        if (command.Status == "completed")
        {
            return Ok(new
            {
                success = true,
                command.Id,
                command.Status,
                command.ErrorMessage
            });
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

    [HttpPost("{commandId:guid}/complete")]
    public async Task<IActionResult> MarkRemoteCommandCompleted(
        Guid commandId,
        AgentRemoteCommandCompleteDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();
        var command = await _db.RemoteCommands
            .FirstOrDefaultAsync(x =>
                x.Id == commandId &&
                x.DeviceCode == deviceCode);

        if (command == null)
        {
            return NotFound("Remote command not found for this device.");
        }

        var completableCommandTypes = new[]
        {
            "REMOVE_EPDESK_AGENT",
            "START_SCAN",
            "STOP_SCAN",
            "START_FILE_UPLOAD",
            "STOP_FILE_UPLOAD"
        };

        if (!completableCommandTypes.Contains(command.CommandType))
        {
            return BadRequest(
                "This completion endpoint does not support the command type."
            );
        }

        if (command.Status == "completed")
        {
            return Ok(new
            {
                success = true,
                command.Id,
                command.Status,
                message = dto.Message ?? ""
            });
        }

        if (command.Status != "sent_to_agent" &&
            command.Status != "failed")
        {
            return Conflict(
                $"Cannot complete a command with status '{command.Status}'."
            );
        }

        command.Status = "completed";
        command.CompletedAtUtc = DateTime.UtcNow;
        command.ErrorMessage = "";

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            command.Id,
            command.Status,
            message = dto.Message ?? ""
        });
    }
}

public class AgentRemoteCommandFailDto
{
    public string ErrorMessage { get; set; } = "";
}

public class AgentRemoteCommandCompleteDto
{
    public string DeviceCode { get; set; } = "";

    public string Message { get; set; } = "";
}
