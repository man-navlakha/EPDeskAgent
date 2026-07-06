using EPDeskServerApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
[Route("api/agent/update")]
public class AgentUpdateController : ControllerBase
{
    private readonly AppDbContext _db;

    public AgentUpdateController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestUpdate(
        [FromQuery] string currentVersion,
        [FromQuery] string deviceCode)
    {
        var latest = await _db.AgentVersions
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (latest == null)
        {
            return Ok(new
            {
                updateAvailable = false,
                message = "No active agent version found."
            });
        }

        var updateAvailable = IsNewerVersion(latest.Version, currentVersion);

        return Ok(new
        {
            updateAvailable,
            currentVersion,
            latestVersion = latest.Version,
            downloadUrl = updateAvailable ? latest.DownloadUrl : "",
            sha256 = updateAvailable ? latest.Sha256 : "",
            isMandatory = latest.IsMandatory,
            releaseNotes = latest.ReleaseNotes
        });
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return true;
        }

        if (!Version.TryParse(latestVersion, out var latest))
        {
            return false;
        }

        if (!Version.TryParse(currentVersion, out var current))
        {
            return true;
        }

        return latest > current;
    }
}