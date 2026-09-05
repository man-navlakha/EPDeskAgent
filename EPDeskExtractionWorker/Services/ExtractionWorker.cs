using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Services.Jobs;
using EPDeskExtractionWorker.Services.Processing;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Services;

public sealed class ExtractionWorker : BackgroundService
{
    private readonly ExtractionWorkerOptions _options;
    private readonly IExtractionJobStore _jobStore;
    private readonly TemporaryWorkspaceFactory _workspaceFactory;
    private readonly B2ObjectTransferService _objectTransfer;
    private readonly SandboxExtractionClient _sandbox;
    private readonly ExtractionArtifactBuilder _artifactBuilder;
    private readonly ExtractionWorkerRuntimeState _runtimeState;
    private readonly ILogger<ExtractionWorker> _logger;
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private readonly string _workerId;
    private int _claimedJobCount;

    public ExtractionWorker(
        IOptions<ExtractionWorkerOptions> options,
        IExtractionJobStore jobStore,
        TemporaryWorkspaceFactory workspaceFactory,
        B2ObjectTransferService objectTransfer,
        SandboxExtractionClient sandbox,
        ExtractionArtifactBuilder artifactBuilder,
        ExtractionWorkerRuntimeState runtimeState,
        ILogger<ExtractionWorker> logger)
    {
        _options = options.Value;
        _jobStore = jobStore;
        _workspaceFactory = workspaceFactory;
        _objectTransfer = objectTransfer;
        _sandbox = sandbox;
        _artifactBuilder = artifactBuilder;
        _runtimeState = runtimeState;
        _logger = logger;
        _workerId = CreateWorkerId();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runtimeState.Start(_options.ProcessingEnabled, _workerId);
        if (!_options.ProcessingEnabled)
        {
            _logger.LogInformation(
                "Extraction worker is healthy but processing is disabled. Worker: {WorkerId}",
                _workerId
            );
            await WaitForShutdownAsync(stoppingToken);
            return;
        }

        _logger.LogInformation(
            "Extraction processing started. Worker: {WorkerId}; pipeline: {PipelineVersion}; " +
            "concurrency: {MaxConcurrentJobs}; lifetime claim limit: {LifetimeLimit}.",
            _workerId,
            _options.PipelineVersion,
            _options.MaxConcurrentJobs,
            _options.MaxJobsPerInstanceLifetime
        );

        var slots = Enumerable.Range(0, _options.MaxConcurrentJobs)
            .Select(slot => RunSlotAsync(slot, stoppingToken))
            .ToArray();
        await Task.WhenAll(slots);
    }

    private async Task RunSlotAsync(int slot, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimedExtractionJob? job;
            try
            {
                job = await TryClaimWithinLimitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Could not poll the extraction queue. Worker slot: {Slot}",
                    slot
                );
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.PollIntervalSeconds),
                    stoppingToken
                );
                continue;
            }
            if (job is null)
            {
                if (LifetimeLimitReached())
                {
                    _runtimeState.SetCanaryLimitReached();
                    _logger.LogInformation(
                        "Worker slot {Slot} is idle because the lifetime canary limit was reached.",
                        slot
                    );
                    await WaitForShutdownAsync(stoppingToken);
                    return;
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.PollIntervalSeconds),
                    stoppingToken
                );
                continue;
            }

            _runtimeState.JobStarted(job.JobId);
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            finally
            {
                _runtimeState.JobFinished(job.JobId);
            }
        }
    }

    private async Task<ClaimedExtractionJob?> TryClaimWithinLimitAsync(
        CancellationToken cancellationToken)
    {
        await _claimGate.WaitAsync(cancellationToken);
        try
        {
            if (LifetimeLimitReached())
            {
                return null;
            }

            var claimed = await _jobStore.TryClaimAsync(
                new ClaimExtractionJobRequest
                {
                    LeaseOwner = _workerId,
                    PipelineVersion = _options.PipelineVersion,
                    LeaseDuration = TimeSpan.FromSeconds(_options.LeaseSeconds),
                    AllowedJobId = ParseCanaryJobId(_options.CanaryJobId),
                    RequireTrustedSourceIdentity = _options.RequireTrustedSourceIdentity,
                    AllowedExtensions = _options.AllowedExtensions
                        .Select(extension => extension.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                },
                cancellationToken
            );

            if (claimed is not null)
            {
                Interlocked.Increment(ref _claimedJobCount);
                _runtimeState.JobClaimed();
            }

            return claimed;
        }
        finally
        {
            _claimGate.Release();
        }
    }

    private async Task ProcessJobAsync(
        ClaimedExtractionJob job,
        CancellationToken stoppingToken)
    {
        using var processingTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        processingTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ProcessingTimeoutSeconds));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = 0;
        var heartbeat = HeartbeatAsync(
            job,
            heartbeatStop.Token,
            () =>
            {
                Interlocked.Exchange(ref leaseLost, 1);
                processingTimeout.Cancel();
            }
        );

        try
        {
            if (job.IsDeleted)
            {
                throw new RejectedExtractionException(
                    "document_deleted",
                    "The source document was deleted before extraction began."
                );
            }

            if (!await _jobStore.MarkProcessingAsync(job.Lease, processingTimeout.Token))
            {
                throw new ExtractionLeaseLostException();
            }

            await using var workspace = _workspaceFactory.Create(job.JobId, job.LeaseToken);
            var sourcePath = workspace.SourceFilePath(job.FileName);
            var downloaded = await _objectTransfer.DownloadAsync(
                job.BucketName,
                job.ObjectKey,
                job.B2VersionId,
                sourcePath,
                job.SizeBytes,
                _options.MaxDownloadBytes,
                processingTimeout.Token
            );

            VerifyObjectIdentity(job, downloaded);
            var sandboxResult = await _sandbox.ExtractAsync(
                sourcePath,
                job.FileName,
                job.DeclaredContentType,
                processingTimeout.Token
            );
            var inspection = new FileInspection(
                Path.GetExtension(job.FileName).ToLowerInvariant(),
                sandboxResult.DetectedContentType,
                job.DeclaredContentType.Trim()
            );

            var prepared = await _artifactBuilder.PrepareAsync(
                job,
                sandboxResult.ExtractedDocument,
                inspection,
                downloaded,
                workspace.DerivativeFilePath(),
                processingTimeout.Token
            );
            var uploaded = await _objectTransfer.UploadDerivativeAsync(
                prepared.ObjectKey,
                prepared.FilePath,
                "application/json",
                processingTimeout.Token
            );
            var successful = ExtractionArtifactBuilder.BuildSuccessfulExtraction(
                prepared,
                inspection,
                downloaded,
                uploaded
            );

            if (!await _jobStore.CompleteAsync(
                    job.Lease,
                    successful,
                    processingTimeout.Token))
            {
                throw new ExtractionLeaseLostException();
            }

            _runtimeState.JobSucceeded(job.JobId);
            _logger.LogInformation(
                "Extraction completed. Job: {JobId}; version: {DocumentVersionId}; " +
                "sections: {SectionCount}; SHA-256: {Sha256}.",
                job.JobId,
                job.DocumentVersionId,
                successful.Sections.Count,
                downloaded.Sha256
            );
        }
        catch (ExtractionLeaseLostException)
        {
            Interlocked.Exchange(ref leaseLost, 1);
            _runtimeState.JobLeaseLost(job.JobId);
            _logger.LogWarning(
                "Stopped extraction because the lease was lost. Job: {JobId}",
                job.JobId
            );
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await ReleaseForShutdownAsync(job);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref leaseLost) == 1)
        {
            _runtimeState.JobLeaseLost(job.JobId);
            _logger.LogWarning(
                "Stopped extraction after a heartbeat lost ownership. Job: {JobId}",
                job.JobId
            );
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(
                job,
                ExtractionFailureDisposition.Retry,
                "processing_timeout",
                "Document processing exceeded its configured time limit.",
                stoppingToken
            );
        }
        catch (RejectedExtractionException exception)
        {
            await RecordFailureAsync(
                job,
                ExtractionFailureDisposition.Reject,
                exception.ErrorCode,
                exception.Message,
                stoppingToken
            );
        }
        catch (RetryableExtractionException exception)
        {
            await RecordFailureAsync(
                job,
                ExtractionFailureDisposition.Retry,
                exception.ErrorCode,
                exception.Message,
                stoppingToken
            );
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected extraction failure. Job: {JobId}", job.JobId);
            await RecordFailureAsync(
                job,
                ExtractionFailureDisposition.Retry,
                "unexpected_processing_error",
                "An unexpected error occurred while processing the document.",
                stoppingToken
            );
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected when processing or host shutdown ends the heartbeat.
            }
        }
    }

    private async Task HeartbeatAsync(
        ClaimedExtractionJob job,
        CancellationToken cancellationToken,
        Action leaseLost)
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.HeartbeatSeconds),
                    cancellationToken
                );
                var renewed = await _jobStore.HeartbeatAsync(
                    job.Lease,
                    TimeSpan.FromSeconds(_options.LeaseSeconds),
                    cancellationToken
                );
                if (!renewed)
                {
                    leaseLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal completion/shutdown path.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Extraction heartbeat failed. Job: {JobId}", job.JobId);
            leaseLost();
        }
    }

    private async Task RecordFailureAsync(
        ClaimedExtractionJob job,
        ExtractionFailureDisposition disposition,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        ExtractionFailureTransition? transition;
        try
        {
            transition = await _jobStore.FailAsync(
                job.Lease,
                new ExtractionFailure
                {
                    ErrorCode = Truncate(errorCode, 128),
                    ErrorMessage = Truncate(errorMessage, 4000),
                    Disposition = disposition,
                    InitialRetryDelay = TimeSpan.FromSeconds(_options.RetryBaseSeconds)
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not persist the extraction failure transition. Job: {JobId}",
                job.JobId
            );
            return;
        }

        if (transition is null)
        {
            _runtimeState.JobLeaseLost(job.JobId);
            _logger.LogWarning(
                "Could not record failure because the job lease was no longer owned. Job: {JobId}",
                job.JobId
            );
            return;
        }

        _runtimeState.JobFailed(job.JobId, transition.Status, errorCode);
        _logger.LogWarning(
            "Extraction did not complete. Job: {JobId}; status: {Status}; " +
            "attempt: {AttemptCount}/{MaxAttempts}; error: {ErrorCode}.",
            job.JobId,
            transition.Status,
            transition.AttemptCount,
            transition.MaxAttempts,
            errorCode
        );
    }

    private async Task ReleaseForShutdownAsync(ClaimedExtractionJob job)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _jobStore.FailAsync(
                job.Lease,
                new ExtractionFailure
                {
                    ErrorCode = "worker_shutdown",
                    ErrorMessage = "Worker shutdown interrupted document processing.",
                    Disposition = ExtractionFailureDisposition.Release,
                    InitialRetryDelay = TimeSpan.FromSeconds(5),
                    MaximumRetryDelay = TimeSpan.FromSeconds(5)
                },
                timeout.Token
            );
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not release the job lease during shutdown. Job: {JobId}",
                job.JobId
            );
        }
    }

    internal static void VerifyObjectIdentity(
        ClaimedExtractionJob job,
        DownloadedObject downloaded)
    {
        if (string.IsNullOrWhiteSpace(job.B2VersionId) &&
            string.IsNullOrWhiteSpace(downloaded.VersionId))
        {
            throw new RetryableExtractionException(
                "source_version_identity_missing",
                "B2 did not return a version ID for the unpinned source object."
            );
        }

        if (!string.IsNullOrWhiteSpace(job.B2VersionId) &&
            (string.IsNullOrWhiteSpace(downloaded.VersionId) ||
             !string.Equals(job.B2VersionId, downloaded.VersionId, StringComparison.Ordinal)))
        {
            throw new RetryableExtractionException(
                "source_version_mismatch",
                "B2 returned a different object version than the queued immutable version."
            );
        }

        if (!string.IsNullOrWhiteSpace(job.ObjectETag) &&
            (string.IsNullOrWhiteSpace(downloaded.ETag) ||
             !string.Equals(job.ObjectETag.Trim('"'), downloaded.ETag.Trim('"'),
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new RetryableExtractionException(
                "source_etag_mismatch",
                "B2 returned an ETag that differs from the queued object revision."
            );
        }

        if (!string.IsNullOrWhiteSpace(job.Sha256) &&
            !string.Equals(job.Sha256, downloaded.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RejectedExtractionException(
                "checksum_mismatch",
                "The downloaded SHA-256 checksum does not match the stored source checksum."
            );
        }
    }

    private bool LifetimeLimitReached() =>
        _options.MaxJobsPerInstanceLifetime > 0 &&
        Volatile.Read(ref _claimedJobCount) >= _options.MaxJobsPerInstanceLifetime;

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private static string CreateWorkerId()
    {
        var machine = new string(Environment.MachineName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        if (machine.Length == 0)
        {
            machine = "worker";
        }

        return $"{machine}-{Guid.NewGuid():N}";
    }

    private static Guid? ParseCanaryJobId(string value) =>
        Guid.TryParse(value, out var jobId) && jobId != Guid.Empty ? jobId : null;

    private static string Truncate(string value, int maximumLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed class ExtractionLeaseLostException : Exception
    {
    }
}
