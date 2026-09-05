namespace EPDeskServerApi.Models;

/// <summary>
/// Durable PostgreSQL queue record claimed by the Railway extraction worker.
/// LeaseToken prevents a stale worker from completing a job after its lease has
/// been reassigned.
/// </summary>
public sealed class ExtractionJob
{
    public Guid Id { get; set; }

    public Guid DocumentVersionId { get; set; }

    public DocumentVersion DocumentVersion { get; set; } = null!;

    public string PipelineVersion { get; set; } = "v1";

    // queued, running, retry_wait, completed, rejected, dead_letter
    public string Status { get; set; } = "queued";

    public int Priority { get; set; }

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; } = 5;

    public string LeaseOwner { get; set; } = "";

    public string LeaseToken { get; set; } = "";

    public DateTime? LeaseUntilUtc { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;

    public string ErrorCode { get; set; } = "";

    public string ErrorMessage { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
