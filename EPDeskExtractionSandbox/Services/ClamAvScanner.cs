using System.Globalization;
using System.Net.Sockets;
using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public sealed class ClamAvScanner
{
    private readonly SandboxOptions _options;
    private readonly IClamAvDaemonClient _daemon;

    public ClamAvScanner(
        IOptions<SandboxOptions> options,
        IClamAvDaemonClient daemon)
    {
        _options = options.Value;
        _daemon = daemon;
    }

    public async Task<ClamAvReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            _options.ClamDaemonConnectTimeoutSeconds
        ));
        try
        {
            var ping = await _daemon.PingAsync(timeout.Token);
            if (!string.Equals(ping.Trim(), "PONG", StringComparison.Ordinal))
            {
                return new ClamAvReadiness(false, "The ClamAV daemon did not answer its health probe.");
            }

            var version = await _daemon.VersionAsync(timeout.Token);
            if (!TryParseLoadedVersion(version, out var loaded))
            {
                return new ClamAvReadiness(
                    false,
                    "The ClamAV daemon returned an invalid loaded-signature version."
                );
            }

            if (!Version.TryParse(_options.MinimumClamVersion, out var minimumVersion) ||
                loaded.EngineVersion < minimumVersion)
            {
                return new ClamAvReadiness(
                    false,
                    "The loaded ClamAV engine is below the supported minimum version."
                );
            }

            var signatureAge = DateTimeOffset.UtcNow - loaded.SignatureBuildTime;
            if (signatureAge < TimeSpan.FromMinutes(-5))
            {
                return new ClamAvReadiness(
                    false,
                    "The loaded ClamAV signatures have an invalid future timestamp."
                );
            }

            return signatureAge <= TimeSpan.FromHours(_options.MaxClamSignatureAgeHours)
                ? new ClamAvReadiness(true, "ClamAV daemon and loaded signatures are ready.")
                : new ClamAvReadiness(false, "The loaded ClamAV signatures are stale.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or SocketException or IOException)
        {
            return new ClamAvReadiness(false, "The ClamAV daemon is unavailable.");
        }
    }

    public async Task ScanAsync(string filePath, CancellationToken cancellationToken)
    {
        var readiness = await CheckReadinessAsync(cancellationToken);
        if (!readiness.IsReady)
        {
            throw new SandboxRequestException(
                503,
                "malware_scanner_not_ready",
                readiness.Description
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.MalwareScanTimeoutSeconds));
        string response;
        try
        {
            response = await _daemon.ScanFileAsync(
                filePath,
                _options.MaxInputBytes,
                timeout.Token
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new SandboxRequestException(
                503,
                "malware_scan_timeout",
                "The malware scan timed out."
            );
        }
        catch (ClamAvInputLimitException)
        {
            throw new SandboxRequestException(
                422,
                "malware_scan_limit_exceeded",
                "The file could not be scanned within the configured safety limits."
            );
        }
        catch (SocketException)
        {
            throw new SandboxRequestException(
                503,
                "malware_scanner_not_ready",
                "The ClamAV daemon is unavailable."
            );
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SandboxRequestException(
                503,
                "malware_scan_failed",
                "ClamAV could not complete the scan."
            );
        }

        MapScanResponse(response);
    }

    internal static void MapScanResponse(string response)
    {
        response = response.Trim();
        if (string.Equals(response, "stream: OK", StringComparison.Ordinal))
        {
            return;
        }

        var exceededLimit = response.Contains(
                "Heuristics.Limits.Exceeded",
                StringComparison.OrdinalIgnoreCase
            ) ||
            response.Contains(
                "size limit exceeded",
                StringComparison.OrdinalIgnoreCase
            );
        if (exceededLimit)
        {
            throw new SandboxRequestException(
                422,
                "malware_scan_limit_exceeded",
                "The file could not be scanned within the configured safety limits."
            );
        }

        if (response.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            throw new SandboxRequestException(
                422,
                "malware_detected",
                "The file was rejected by malware scanning."
            );
        }

        throw new SandboxRequestException(
            503,
            "malware_scan_failed",
            "ClamAV could not complete the scan."
        );
    }

    internal static bool TryParseLoadedVersion(
        string response,
        out ClamAvLoadedVersion loadedVersion)
    {
        loadedVersion = default;
        var parts = response.Trim().Split('/', 3, StringSplitOptions.TrimEntries);
        const string enginePrefix = "ClamAV ";
        if (parts.Length != 3 ||
            !parts[0].StartsWith(enginePrefix, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(parts[0][enginePrefix.Length..], out var engineVersion) ||
            !int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var databaseVersion
            ) ||
            databaseVersion <= 0)
        {
            return false;
        }

        var normalizedDate = string.Join(
            ' ',
            parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );
        if (!DateTimeOffset.TryParseExact(
                normalizedDate,
                "ddd MMM d HH:mm:ss yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var signatureBuildTime))
        {
            return false;
        }

        loadedVersion = new ClamAvLoadedVersion(
            engineVersion,
            databaseVersion,
            signatureBuildTime
        );
        return true;
    }
}

internal readonly record struct ClamAvLoadedVersion(
    Version EngineVersion,
    int DatabaseVersion,
    DateTimeOffset SignatureBuildTime);

public sealed record ClamAvReadiness(bool IsReady, string Description);
