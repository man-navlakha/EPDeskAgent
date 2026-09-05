using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EPDeskExtractionSandbox.Configuration;
using EPDeskExtractionSandbox.Health;
using EPDeskExtractionSandbox.Services;
using EPDeskExtractionWorker.Services.Extraction;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<SandboxOptions>()
    .Bind(builder.Configuration.GetSection(SandboxOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SandboxOptions>, SandboxOptionsValidator>();
builder.Services.AddSingleton<RequestConcurrencyGate>();
builder.Services.AddSingleton<RequestFileStore>();
builder.Services.AddSingleton<ExtractionRequestParser>();
builder.Services.AddSingleton<IClamAvDaemonClient, ClamAvDaemonClient>();
builder.Services.AddSingleton<ClamAvScanner>();
builder.Services.AddSingleton<FileTypeInspector>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<SandboxOptions>>().Value;
    var limits = new ExtractionLimits
    {
        MaxInputBytes = options.MaxInputBytes,
        MaxTextCharacters = options.MaxTextCharacters,
        MaxXmlCharacters = options.MaxXmlCharacters,
        MaxArchiveEntries = options.MaxArchiveEntries,
        MaxArchiveEntryBytes = options.MaxArchiveEntryBytes,
        MaxArchiveExpandedBytes = options.MaxArchiveExpandedBytes,
        MaxCompressionRatio = options.MaxCompressionRatio,
        MaxTableRows = options.MaxTableRows,
        MaxTableColumns = options.MaxTableColumns,
        MaxCellCharacters = options.MaxCellCharacters,
        SampleRowCount = options.SampleRowCount,
        MaxExternalOutputCharacters = options.MaxExternalOutputCharacters,
        ExternalProcessTimeout = TimeSpan.FromSeconds(options.ExternalProcessTimeoutSeconds)
    };
    return FileExtractionService.CreateDefault(limits);
});
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<ClamAvReadinessHealthCheck>("clamav", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Ok(new
{
    service = "epdesk-extraction-sandbox",
    endpoints = new[] { "/extract", "/health/live", "/health/ready" }
}));

app.MapPost("/extract", async (
    HttpContext context,
    IOptions<SandboxOptions> configuredOptions,
    RequestConcurrencyGate concurrencyGate,
    RequestFileStore fileStore,
    ExtractionRequestParser requestParser,
    ClamAvScanner scanner,
    FileTypeInspector inspector,
    FileExtractionService extractionService,
    ILoggerFactory loggerFactory) =>
{
    var options = configuredOptions.Value;
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!HasValidApiKey(context.Request, options))
    {
        return Error(401, "unauthorized", "A valid extraction API key is required.");
    }

    if (!concurrencyGate.TryEnter(out var concurrencyLease))
    {
        return Error(429, "sandbox_busy", "The extraction sandbox is currently busy.");
    }

    using (concurrencyLease)
    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        TemporaryRequestFile? temporaryFile = null;
        try
        {
            var parsed = requestParser.Parse(context.Request);
            temporaryFile = await fileStore.WriteAsync(
                context.Request.Body,
                context.Request.ContentLength,
                timeout.Token);

            // Malware scanning must precede MIME inspection because Office type detection opens ZIP metadata.
            await scanner.ScanAsync(temporaryFile.FilePath, timeout.Token);
            var inspection = await inspector.InspectAsync(
                temporaryFile.FilePath,
                parsed.FileName,
                parsed.DeclaredContentType,
                timeout.Token);

            var document = await extractionService.ExtractAsync(
                new ExtractionRequest(
                    temporaryFile.FilePath,
                    parsed.FileName,
                    inspection.DetectedContentType,
                    parsed.IncludeSpeakerNotes,
                    parsed.IncludeWordComments,
                    parsed.EnableImageOcr,
                    parsed.OcrLanguage),
                timeout.Token);

            var response = JsonSerializer.SerializeToUtf8Bytes(
                new ExtractionResponse(inspection.DetectedContentType, document),
                SandboxJson.Options);
            if (response.Length > options.MaxResponseBytes)
            {
                return Error(422, "response_limit_exceeded", "Extracted output exceeds the configured response limit.");
            }

            return Results.Bytes(response, "application/json; charset=utf-8");
        }
        catch (SandboxRequestException exception)
        {
            return Error(exception.StatusCode, exception.ErrorCode, exception.Message);
        }
        catch (ExtractionNeedsOcrException exception)
        {
            return Error(422, "ocr_required", exception.Message);
        }
        catch (ExtractionException exception)
        {
            return Error(422, "extraction_failed", exception.Message);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Error(504, "extraction_timeout", "The extraction request timed out.");
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("ExtractionEndpoint")
                .LogError(exception, "Unhandled extraction sandbox failure.");
            return Error(500, "internal_error", "The extraction sandbox could not process the file.");
        }
        finally
        {
            if (temporaryFile is not null)
            {
                await temporaryFile.DisposeAsync();
            }
        }
    }
});

app.Run();

static bool HasValidApiKey(HttpRequest request, SandboxOptions options)
{
    var supplied = request.Headers[options.ApiKeyHeader].ToString();
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.SharedApiKey));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
}

static IResult Error(int statusCode, string code, string message)
{
    const int maximumMessageCharacters = 1_000;
    var boundedMessage = message.Length <= maximumMessageCharacters
        ? message
        : message[..maximumMessageCharacters];
    return Results.Json(
        new { error = new { code, message = boundedMessage } },
        statusCode: statusCode,
        options: SandboxJson.Options);
}

public sealed record ExtractionResponse(string DetectedContentType, ExtractedDocument ExtractedDocument);

internal static class SandboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

public partial class Program;
