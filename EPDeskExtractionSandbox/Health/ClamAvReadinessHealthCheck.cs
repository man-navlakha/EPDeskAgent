using EPDeskExtractionSandbox.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EPDeskExtractionSandbox.Health;

public sealed class ClamAvReadinessHealthCheck : IHealthCheck
{
    private readonly ClamAvScanner _scanner;

    public ClamAvReadinessHealthCheck(ClamAvScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var readiness = await _scanner.CheckReadinessAsync(cancellationToken);
        return readiness.IsReady
            ? HealthCheckResult.Healthy(readiness.Description)
            : HealthCheckResult.Unhealthy(readiness.Description);
    }
}
