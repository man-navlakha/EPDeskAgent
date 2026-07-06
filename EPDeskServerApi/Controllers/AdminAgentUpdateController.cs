using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/admin/agent-updates")]
public class AdminAgentUpdateController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminAgentUpdateController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> CreateAgentVersion(CreateAgentVersionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Version))
        {
            return BadRequest("Version is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.DownloadUrl))
        {
            return BadRequest("Download URL is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.Sha256))
        {
            return BadRequest("SHA256 hash is required.");
        }

        var existing = await _db.AgentVersions
            .FirstOrDefaultAsync(x => x.Version == dto.Version);

        if (existing != null)
        {
            return BadRequest("This version already exists.");
        }

        if (dto.MakeActive)
        {
            var activeVersions = await _db.AgentVersions
                .Where(x => x.IsActive)
                .ToListAsync();

            foreach (var version in activeVersions)
            {
                version.IsActive = false;
            }
        }

        var agentVersion = new AgentVersion
        {
            Id = Guid.NewGuid(),
            Version = dto.Version,
            DownloadUrl = dto.DownloadUrl,
            Sha256 = dto.Sha256,
            IsMandatory = dto.IsMandatory,
            IsActive = dto.MakeActive,
            ReleaseNotes = dto.ReleaseNotes,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.AgentVersions.Add(agentVersion);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            agentVersion.Id,
            agentVersion.Version,
            agentVersion.IsActive
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetAgentVersions()
    {
        var versions = await _db.AgentVersions
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.Version,
                x.DownloadUrl,
                x.Sha256,
                x.IsMandatory,
                x.IsActive,
                x.ReleaseNotes,
                x.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(versions);
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> ActivateVersion(Guid id)
    {
        var selected = await _db.AgentVersions.FirstOrDefaultAsync(x => x.Id == id);

        if (selected == null)
        {
            return NotFound("Agent version not found.");
        }

        var allVersions = await _db.AgentVersions.ToListAsync();

        foreach (var version in allVersions)
        {
            version.IsActive = false;
        }

        selected.IsActive = true;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            activeVersion = selected.Version
        });
    }
}

public class CreateAgentVersionDto
{
    public string Version { get; set; } = "";

    public string DownloadUrl { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public bool IsMandatory { get; set; } = true;

    public bool MakeActive { get; set; } = true;

    public string ReleaseNotes { get; set; } = "";
}