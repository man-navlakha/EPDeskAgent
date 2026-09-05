using EPDeskMcpServer.Configuration;
using EPDeskMcpServer.Contracts;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskMcpServer.Services;

/// <summary>
/// The one place this service writes. Re-queueing is idempotent and cannot
/// destroy anything: it resets a job so the extraction worker picks it up on
/// its next poll.
/// </summary>
public sealed class ExtractionMaintenanceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EpDeskMcpOptions _options;
    private readonly ILogger<ExtractionMaintenanceService> _logger;

    public ExtractionMaintenanceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<EpDeskMcpOptions> options,
        ILogger<ExtractionMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RequeueResult> RequeueAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableWriteTools)
        {
            throw new McpToolException(
                "Write tools are disabled on this MCP server. Set " +
                "Mcp__EnableWriteTools=true on the service to allow " +
                "re-queueing extractions."
            );
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var version = await db.DocumentVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

        if (version is null)
        {
            throw new McpToolException(
                $"No document version found with id {versionId}. " +
                "epdesk_get_extraction_status lists failing versionId values."
            );
        }

        var job = await db.ExtractionJobs
            .FirstOrDefaultAsync(
                j => j.DocumentVersionId == versionId,
                cancellationToken
            );

        var previousStatus = job?.Status ?? "none";
        var now = DateTime.UtcNow;

        if (job is null)
        {
            job = new ExtractionJob
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = versionId,
                PipelineVersion = string.IsNullOrWhiteSpace(
                    version.ExtractionPipelineVersion)
                    ? DocumentExtractionQueueService.CurrentPipelineVersion
                    : version.ExtractionPipelineVersion,
                CreatedAtUtc = now
            };

            db.ExtractionJobs.Add(job);
        }
        else if (job.Status == "running")
        {
            throw new McpToolException(
                $"Extraction for '{version.FileName}' is currently running " +
                $"(lease held by '{job.LeaseOwner}'). Wait for the lease to " +
                "expire before re-queueing."
            );
        }

        job.Status = "queued";
        job.AttemptCount = 0;
        job.NextAttemptAtUtc = now;
        job.UpdatedAtUtc = now;
        job.ErrorCode = "";
        job.ErrorMessage = "";

        // Clearing the lease is what actually frees the row for the next
        // worker poll; leaving a stale token behind would keep it parked.
        job.LeaseOwner = "";
        job.LeaseToken = "";
        job.LeaseUntilUtc = null;
        job.LastHeartbeatAtUtc = null;
        job.CompletedAtUtc = null;

        version.ExtractionStatus = "queued";
        version.ExtractionErrorCode = "";
        version.ExtractionError = "";
        version.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Re-queued extraction for version {VersionId} ({FileName}); " +
            "previous job status was {PreviousStatus}.",
            versionId,
            version.FileName,
            previousStatus
        );

        return new RequeueResult(
            version.Id,
            version.DocumentId,
            version.FileName,
            previousStatus,
            job.Status,
            job.AttemptCount,
            $"'{version.FileName}' is queued for extraction again. The " +
            "extraction worker picks up queued jobs on its next poll; check " +
            "progress with epdesk_get_extraction_status."
        );
    }
}
