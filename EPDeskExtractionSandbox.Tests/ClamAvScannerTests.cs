using System.Globalization;
using EPDeskExtractionSandbox.Configuration;
using EPDeskExtractionSandbox.Services;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Tests;

public sealed class ClamAvScannerTests
{
    [Fact]
    public async Task CheckReadinessAsync_RequiresResponsiveDaemonWithFreshLoadedSignatures()
    {
        var daemon = new StubDaemon();
        var scanner = CreateScanner(daemon);

        var result = await scanner.CheckReadinessAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(1, daemon.PingCount);
        Assert.Equal(1, daemon.VersionCount);
    }

    [Fact]
    public async Task CheckReadinessAsync_RejectsStaleLoadedSignatures()
    {
        var daemon = new StubDaemon
        {
            VersionResponse = VersionResponse(DateTimeOffset.UtcNow.AddHours(-73))
        };
        var scanner = CreateScanner(daemon);

        var result = await scanner.CheckReadinessAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal(1, daemon.PingCount);
        Assert.Equal(1, daemon.VersionCount);
        Assert.Contains("stale", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckReadinessAsync_RejectsUnsupportedEngine()
    {
        var daemon = new StubDaemon
        {
            VersionResponse = VersionResponse(
                DateTimeOffset.UtcNow,
                engineVersion: "1.0.9"
            )
        };
        var scanner = CreateScanner(daemon);

        var result = await scanner.CheckReadinessAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("minimum", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckReadinessAsync_RejectsUnparseableLoadedVersion()
    {
        var daemon = new StubDaemon { VersionResponse = "ClamAV 1.4.3" };
        var scanner = CreateScanner(daemon);

        var result = await scanner.CheckReadinessAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("invalid", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseLoadedVersion_ParsesDaemonFieldsAndRepeatedDateSpaces()
    {
        var parsed = ClamAvScanner.TryParseLoadedVersion(
            "ClamAV 1.4.5/27800/Wed Aug  5 00:00:00 2026",
            out var loaded
        );

        Assert.True(parsed);
        Assert.Equal(new Version(1, 4, 5), loaded.EngineVersion);
        Assert.Equal(27800, loaded.DatabaseVersion);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            loaded.SignatureBuildTime
        );
    }

    [Fact]
    public async Task ScanAsync_UsesPersistentDaemonVerdictAfterReadiness()
    {
        var daemon = new StubDaemon { ScanResponse = "stream: OK" };
        var scanner = CreateScanner(daemon);

        await scanner.ScanAsync("temporary-file", CancellationToken.None);

        Assert.Equal(1, daemon.ScanCount);
        Assert.Equal(25L * 1024 * 1024, daemon.LastMaximumBytes);
    }

    [Fact]
    public async Task ScanAsync_MapsDaemonMalwareVerdictWithoutExposingSignature()
    {
        var daemon = new StubDaemon
        {
            ScanResponse = "stream: Win.Test.EICAR_HDB-1 FOUND"
        };
        var scanner = CreateScanner(daemon);

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            scanner.ScanAsync("temporary-file", CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("malware_detected", exception.ErrorCode);
        Assert.DoesNotContain("EICAR", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ClamAvScanner CreateScanner(IClamAvDaemonClient daemon) => new(
        Options.Create(new SandboxOptions
        {
            MinimumClamVersion = "1.4.5",
            MaxClamSignatureAgeHours = 72,
            ClamDaemonConnectTimeoutSeconds = 5,
            MalwareScanTimeoutSeconds = 120,
            MaxInputBytes = 25L * 1024 * 1024
        }),
        daemon
    );

    private static string VersionResponse(
        DateTimeOffset signatureBuildTime,
        string engineVersion = "1.4.5") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ClamAV {engineVersion}/27800/{signatureBuildTime.UtcDateTime:ddd MMM d HH:mm:ss yyyy}"
        );

    private sealed class StubDaemon : IClamAvDaemonClient
    {
        public int PingCount { get; private set; }
        public int VersionCount { get; private set; }
        public int ScanCount { get; private set; }
        public long LastMaximumBytes { get; private set; }
        public string VersionResponse { get; init; } = ClamAvScannerTests.VersionResponse(
            DateTimeOffset.UtcNow
        );
        public string ScanResponse { get; init; } = "stream: OK";

        public Task<string> PingAsync(CancellationToken cancellationToken)
        {
            PingCount++;
            return Task.FromResult("PONG");
        }

        public Task<string> VersionAsync(CancellationToken cancellationToken)
        {
            VersionCount++;
            return Task.FromResult(VersionResponse);
        }

        public Task<string> ScanFileAsync(
            string filePath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            ScanCount++;
            LastMaximumBytes = maximumBytes;
            return Task.FromResult(ScanResponse);
        }
    }
}
