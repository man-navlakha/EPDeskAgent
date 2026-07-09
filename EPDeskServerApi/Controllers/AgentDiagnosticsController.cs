using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EPDeskServerApi.Controllers;

[ApiController]
public class AgentDiagnosticsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AgentDiagnosticsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("api/agent/diagnostics")]
    public async Task<IActionResult> UploadDiagnostics(UploadDiagnosticsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
        {
            return BadRequest("Device code is required.");
        }

        var deviceCode = dto.DeviceCode.Trim().ToUpperInvariant();

        var report = new DeviceDiagnosticReport
        {
            Id = Guid.NewGuid(),
            DeviceCode = deviceCode,
            CommandId = dto.CommandId,
            AgentVersion = dto.AgentVersion ?? "",
            ServiceStatus = dto.ServiceStatus ?? "",
            WindowsVersion = dto.WindowsVersion ?? "",
            ServiceAccount = dto.ServiceAccount ?? "",
            InternetWorking = dto.InternetWorking,
            ApiReachable = dto.ApiReachable,
            SystemDriveFreeBytes = dto.SystemDriveFreeBytes,
            CurrentRunningTask = dto.CurrentRunningTask ?? "",
            LastFileRequest = dto.LastFileRequest ?? "",
            LastError = dto.LastError ?? "",
            LastHeartbeatUtc = dto.LastHeartbeatUtc,
            PendingRequestsCount = dto.PendingRequestsCount,
            DetailsJson = dto.DetailsJson ?? "",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.DeviceDiagnosticReports.Add(report);

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
            report.Id,
            report.DeviceCode,
            report.CreatedAtUtc
        });
    }

    [HttpGet("api/admin/devices/{deviceCode}/diagnostics")]
    public async Task<IActionResult> GetDeviceDiagnostics(
        string deviceCode,
        [FromQuery] int take = 20)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest("Device code is required.");
        }

        if (take <= 0)
        {
            take = 20;
        }

        if (take > 100)
        {
            take = 100;
        }

        var normalizedDeviceCode = deviceCode.Trim().ToUpperInvariant();

        var reports = await _db.DeviceDiagnosticReports
            .Where(x => x.DeviceCode == normalizedDeviceCode)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.CommandId,
                x.AgentVersion,
                x.ServiceStatus,
                x.WindowsVersion,
                x.ServiceAccount,
                x.InternetWorking,
                x.ApiReachable,
                x.SystemDriveFreeBytes,
                x.CurrentRunningTask,
                x.LastFileRequest,
                x.LastError,
                x.LastHeartbeatUtc,
                x.PendingRequestsCount,
                x.DetailsJson,
                x.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(reports);
    }
}

public class UploadDiagnosticsDto
{
    public string DeviceCode { get; set; } = "";

    public Guid? CommandId { get; set; }

    public string AgentVersion { get; set; } = "";

    public string ServiceStatus { get; set; } = "";

    public string WindowsVersion { get; set; } = "";

    public string ServiceAccount { get; set; } = "";

    public bool InternetWorking { get; set; }

    public bool ApiReachable { get; set; }

    public long SystemDriveFreeBytes { get; set; }

    public string CurrentRunningTask { get; set; } = "";

    public string LastFileRequest { get; set; } = "";

    public string LastError { get; set; } = "";

    public DateTime? LastHeartbeatUtc { get; set; }

    public int PendingRequestsCount { get; set; }

    public string DetailsJson { get; set; } = "";
}