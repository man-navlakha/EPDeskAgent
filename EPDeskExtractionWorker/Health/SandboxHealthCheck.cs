using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Services.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Health;

public sealed class SandboxHealthCheck : IHealthCheck
{
    private readonly ExtractionWorkerOptions _options;
    private readonly SandboxExtractionClient _sandbox;

    public SandboxHealthCheck(
        IOptions<ExtractionWorkerOptions> options,
        SandboxExtractionClient sandbox)
    {
        _options = options.Value;
        _sandbox = sandbox;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ProcessingEnabled)
        {
            return HealthCheckResult.Healthy(
                "The sandbox is not required while processing is disabled."
            );
        }

        try
        {
            Directory.CreateDirectory(Path.GetFullPath(_options.TempRoot));
        }
        catch
        {
            return HealthCheckResult.Unhealthy(
                "The worker temporary root is not writable."
            );
        }

        return await _sandbox.IsReadyAsync(cancellationToken)
            ? HealthCheckResult.Healthy("The extraction sandbox is ready.")
            : HealthCheckResult.Unhealthy("The extraction sandbox is not ready.");
    }
}
