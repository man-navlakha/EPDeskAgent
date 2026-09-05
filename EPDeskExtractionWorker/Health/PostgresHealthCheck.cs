using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EPDeskExtractionWorker.Health;

public sealed class PostgresHealthCheck(
    NpgsqlDataSource dataSource
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt32(result) == 1
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL returned an unexpected result.");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed.");
        }
    }
}
