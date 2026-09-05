namespace EPDeskExtractionWorker.Services.Jobs;

public interface IExtractionJobStore
{
    Task<ClaimedExtractionJob?> TryClaimAsync(
        ClaimExtractionJobRequest request,
        CancellationToken cancellationToken
    );

    Task<bool> HeartbeatAsync(
        ExtractionJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    Task<bool> MarkProcessingAsync(
        ExtractionJobLease lease,
        CancellationToken cancellationToken
    );

    Task<bool> CompleteAsync(
        ExtractionJobLease lease,
        SuccessfulExtraction extraction,
        CancellationToken cancellationToken
    );

    Task<ExtractionFailureTransition?> FailAsync(
        ExtractionJobLease lease,
        ExtractionFailure failure,
        CancellationToken cancellationToken
    );
}
