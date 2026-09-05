using System.Collections.Concurrent;

namespace EPDeskExtractionWorker.Services;

public sealed class ExtractionWorkerRuntimeState
{
    private readonly ConcurrentDictionary<Guid, byte> _activeJobs = new();
    private readonly object _sync = new();
    private string _workerId = "";
    private string _state = "starting";
    private Guid? _lastJobId;
    private string _lastOutcome = "";
    private string _lastErrorCode = "";
    private DateTime? _lastUpdatedAtUtc;
    private int _claimed;
    private int _completed;
    private int _failed;

    public void Start(bool processingEnabled, string workerId)
    {
        lock (_sync)
        {
            _workerId = workerId;
            _state = processingEnabled ? "polling" : "disabled";
            _lastUpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public void JobClaimed() => Interlocked.Increment(ref _claimed);

    public void JobStarted(Guid jobId)
    {
        _activeJobs[jobId] = 0;
        SetLast(jobId, "running", "");
    }

    public void JobFinished(Guid jobId) => _activeJobs.TryRemove(jobId, out _);

    public void JobSucceeded(Guid jobId)
    {
        Interlocked.Increment(ref _completed);
        SetLast(jobId, "completed", "");
    }

    public void JobFailed(Guid jobId, string outcome, string errorCode)
    {
        Interlocked.Increment(ref _failed);
        SetLast(jobId, outcome, errorCode);
    }

    public void JobLeaseLost(Guid jobId)
    {
        Interlocked.Increment(ref _failed);
        SetLast(jobId, "lease_lost", "lease_lost");
    }

    public void SetCanaryLimitReached()
    {
        lock (_sync)
        {
            _state = "canary_limit_reached";
            _lastUpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                workerId = _workerId,
                state = _state,
                claimedJobs = Volatile.Read(ref _claimed),
                completedJobs = Volatile.Read(ref _completed),
                failedJobs = Volatile.Read(ref _failed),
                activeJobIds = _activeJobs.Keys.Order().ToArray(),
                lastJobId = _lastJobId,
                lastOutcome = _lastOutcome,
                lastErrorCode = _lastErrorCode,
                lastUpdatedAtUtc = _lastUpdatedAtUtc
            };
        }
    }

    private void SetLast(Guid jobId, string outcome, string errorCode)
    {
        lock (_sync)
        {
            _lastJobId = jobId;
            _lastOutcome = outcome;
            _lastErrorCode = errorCode;
            _lastUpdatedAtUtc = DateTime.UtcNow;
        }
    }
}
