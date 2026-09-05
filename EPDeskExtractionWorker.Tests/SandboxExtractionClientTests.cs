using System.Net;
using System.Text;
using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Services.Processing;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Tests;

public sealed class SandboxExtractionClientTests
{
    [Fact]
    public async Task ExtractAsync_StreamsAuthenticatedFileAndParsesResult()
    {
        using var files = new TestFiles();
        var path = files.WriteText("source.txt", "hello sandbox");
        string? requestBody = null;
        string? fileName = null;
        string? apiKey = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            fileName = Assert.Single(request.Headers.GetValues("X-File-Name"));
            apiKey = Assert.Single(request.Headers.GetValues("X-Extraction-Key"));
            Assert.Equal("text/plain", request.Content.Headers.ContentType?.MediaType);
            Assert.Equal("false", Assert.Single(
                request.Headers.GetValues("X-Include-Word-Comments")));

            return JsonResponse(HttpStatusCode.OK, """
                {
                  "detected_content_type": "text/plain",
                  "extracted_document": {
                    "format": "text",
                    "sections": [{
                      "section_type": "text",
                      "section_number": 1,
                      "heading": null,
                      "content": "hello sandbox",
                      "metadata": {}
                    }],
                    "metadata": {"page_count": 1},
                    "warnings": []
                  }
                }
                """);
        });
        var options = CreateOptions();
        var client = new SandboxExtractionClient(
            new HttpClient(handler),
            Options.Create(options)
        );

        var result = await client.ExtractAsync(
            path,
            "source.txt",
            "text/plain",
            CancellationToken.None
        );

        Assert.Equal("hello sandbox", requestBody);
        Assert.Equal("source.txt", fileName);
        Assert.Equal(options.SandboxApiKey, apiKey);
        Assert.Equal("text/plain", result.DetectedContentType);
        Assert.Equal("text", result.ExtractedDocument.Format);
        Assert.Equal("hello sandbox", Assert.Single(result.ExtractedDocument.Sections).Content);
    }

    [Fact]
    public async Task ExtractAsync_MapsUnprocessableDocumentToRejectedFailure()
    {
        using var files = new TestFiles();
        var path = files.WriteText("source.txt", "invalid");
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"invalid_document","message":"Document is invalid."}}"""
        )));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RejectedExtractionException>(() =>
            client.ExtractAsync(
                path,
                "source.txt",
                "text/plain",
                CancellationToken.None
            ));

        Assert.Equal("invalid_document", exception.ErrorCode);
        Assert.Equal("Document is invalid.", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_MapsUnavailableSandboxToRetryableFailure()
    {
        using var files = new TestFiles();
        var path = files.WriteText("source.txt", "hello");
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"scanner_unavailable","message":"Scanner unavailable."}}"""
        )));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<RetryableExtractionException>(() =>
            client.ExtractAsync(
                path,
                "source.txt",
                "text/plain",
                CancellationToken.None
            ));

        Assert.Equal("scanner_unavailable", exception.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsync_RejectsResponseOverConfiguredBound()
    {
        using var files = new TestFiles();
        var path = files.WriteText("source.txt", "hello");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[11])
        };
        var handler = new StubHandler((_, _) => Task.FromResult(response));
        var options = CreateOptions();
        options.MaxSandboxResponseBytes = 10;
        var client = new SandboxExtractionClient(
            new HttpClient(handler),
            Options.Create(options)
        );

        var exception = await Assert.ThrowsAsync<RetryableExtractionException>(() =>
            client.ExtractAsync(
                path,
                "source.txt",
                "text/plain",
                CancellationToken.None
            ));

        Assert.Equal("sandbox_response_too_large", exception.ErrorCode);
    }

    [Theory]
    [InlineData("http://sandbox.railway.internal:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://sandbox.example.com", true)]
    [InlineData("http://sandbox.example.com", false)]
    [InlineData("http://sandbox.railway.internal.evil.example", false)]
    public void EndpointPolicy_OnlyAllowsEncryptedOrPrivateHttp(
        string value,
        bool expected)
    {
        Assert.Equal(
            expected,
            SandboxEndpointPolicy.TryCreateSafeBaseUri(value, out _)
        );
    }

    private static SandboxExtractionClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(CreateOptions()));

    private static ExtractionWorkerOptions CreateOptions() => new()
    {
        SandboxBaseUrl = "http://sandbox.railway.internal:8080",
        SandboxApiKey = new string('s', 32),
        MaxSandboxResponseBytes = 1024 * 1024
    };

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
