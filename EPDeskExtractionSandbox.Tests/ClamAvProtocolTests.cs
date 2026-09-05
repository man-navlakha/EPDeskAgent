using System.Text;
using EPDeskExtractionSandbox.Services;

namespace EPDeskExtractionSandbox.Tests;

public sealed class ClamAvProtocolTests
{
    [Fact]
    public async Task WriteInstreamAsync_UsesNullCommandAndBigEndianChunks()
    {
        await using var source = new MemoryStream("abc"u8.ToArray());
        await using var daemon = new MemoryStream();

        await ClamAvProtocol.WriteInstreamAsync(
            daemon,
            source,
            maximumBytes: 3,
            CancellationToken.None
        );

        var expected = "zINSTREAM\0"u8.ToArray()
            .Concat(new byte[] { 0, 0, 0, 3 })
            .Concat("abc"u8.ToArray())
            .Concat(new byte[] { 0, 0, 0, 0 })
            .ToArray();
        Assert.Equal(expected, daemon.ToArray());
    }

    [Fact]
    public async Task WriteInstreamAsync_StopsBeforeSendingBytesOverLimit()
    {
        await using var source = new MemoryStream("abcd"u8.ToArray());
        await using var daemon = new MemoryStream();

        await Assert.ThrowsAsync<ClamAvInputLimitException>(() =>
            ClamAvProtocol.WriteInstreamAsync(
                daemon,
                source,
                maximumBytes: 3,
                CancellationToken.None
            ));
    }

    [Fact]
    public async Task ReadRecordAsync_RequiresNullTerminatedBoundedResponse()
    {
        await using var valid = new MemoryStream(
            Encoding.UTF8.GetBytes("stream: OK\0ignored")
        );
        Assert.Equal(
            "stream: OK",
            await ClamAvProtocol.ReadRecordAsync(valid, CancellationToken.None)
        );

        await using var incomplete = new MemoryStream(Encoding.UTF8.GetBytes("stream: OK"));
        await Assert.ThrowsAsync<ClamAvProtocolException>(() =>
            ClamAvProtocol.ReadRecordAsync(incomplete, CancellationToken.None));
    }

    [Theory]
    [InlineData("stream: Eicar-Signature FOUND", 422, "malware_detected")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxFileSize FOUND", 422, "malware_scan_limit_exceeded")]
    [InlineData("INSTREAM size limit exceeded. ERROR", 422, "malware_scan_limit_exceeded")]
    [InlineData("stream: scan failed ERROR", 503, "malware_scan_failed")]
    [InlineData("unexpected", 503, "malware_scan_failed")]
    [InlineData("unexpected OK", 503, "malware_scan_failed")]
    public void MapScanResponse_FailsClosed(
        string response,
        int expectedStatus,
        string expectedCode)
    {
        var exception = Assert.Throws<SandboxRequestException>(() =>
            ClamAvScanner.MapScanResponse(response));

        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedCode, exception.ErrorCode);
    }

    [Fact]
    public void MapScanResponse_AcceptsOnlyExplicitCleanResult()
    {
        ClamAvScanner.MapScanResponse("stream: OK");
    }
}
