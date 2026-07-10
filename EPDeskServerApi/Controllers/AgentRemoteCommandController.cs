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

public class AgentRemoteCommandFailDto
{
    public string ErrorMessage { get; set; } = "";
}