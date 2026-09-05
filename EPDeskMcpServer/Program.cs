using EPDeskMcpServer.Configuration;
using EPDeskMcpServer.Services;
using EPDeskMcpServer.Tools;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services.Storage;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

const string McpEndpoint = "/mcp";

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<B2StorageOptions>(
    builder.Configuration.GetSection(B2StorageOptions.SectionName)
);

builder.Services
    .AddOptions<EpDeskMcpOptions>()
    .Bind(builder.Configuration.GetSection(EpDeskMcpOptions.SectionName))
    .Validate(
        options => options.MaxPageSize >= options.DefaultPageSize,
        "Mcp:MaxPageSize must be greater than or equal to Mcp:DefaultPageSize."
    )
    .ValidateOnStart();

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings__DefaultConnection is required. Point it at the " +
        "same PostgreSQL database the EPDesk server API uses."
    );
}

// A factory rather than a scoped context: MCP tool calls are served
// concurrently on one connection, and each tool owns its context for the
// length of one call.
builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddSingleton<IObjectStorageService, B2ObjectStorageService>();
builder.Services.AddSingleton<DocumentQueryService>();
builder.Services.AddSingleton<FileAccessService>();
builder.Services.AddSingleton<InsightsService>();
builder.Services.AddSingleton<ExtractionMaintenanceService>();
builder.Services.AddSingleton<ToolPaging>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "epdesk-files",
            Title = "EPDesk File Intelligence",
            Version = typeof(Program).Assembly
                .GetName().Version?.ToString() ?? "1.0.0"
        };

        options.ServerInstructions =
            """
            EPDesk indexes files collected from company Windows machines. Two
            ingestion sources feed one corpus: the agent running on staff
            laptops (automatic_upload) and the bulk import of older device
            data (old_user_data). Both are reachable through the same tools.

            This server exposes metadata and file access only. It answers
            "what files do we have, where did they come from, and give me
            that file". It does not read or search what is inside a file.

            Three ids tie the tools together:
              documentId  - one logical file, stable across revisions
              versionId   - one immutable revision of that file
              derivativeId- one artefact stored against a version

            Typical routes through the tools:
              Find a file         epdesk_list_documents with nameContains,
                                  pathContains, deviceCode or extension
              Inspect it          epdesk_get_document, then
                                  epdesk_get_file_metadata
              Hand it over        epdesk_get_download_url
              Read raw bytes      epdesk_read_file_content, text-like files
              Understand the set  epdesk_get_storage_stats,
                                  epdesk_list_devices
              Confirm it arrived  epdesk_list_uploads

            There is no full-text search. A file can only be located by name,
            Windows path, device, source, type, size, or date. When a request
            describes what is inside a file rather than what it is called,
            say that contents are not searchable and narrow by metadata
            instead of guessing at filenames.

            Every stored file can be downloaded, whatever its type.
            epdesk_get_download_url returns a short-lived private link for any
            document, version, or derivative, and it is the way to give a
            person the real file.

            Results are paged. When a response carries a nextOffset, there is
            more to read; call again with offset set to that value rather than
            assuming the first page is everything.

            This data is private company material. Download links are bearer
            credentials for a single file and should be treated as sensitive.
            """;
    })
    .WithHttpTransport(options =>
    {
        // Stateless keeps every request self-contained, so Railway can restart
        // or scale the service without stranding a client mid-session.
        options.Stateless = true;
    })
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (McpException exception)
            {
                // Tool exceptions carry guidance written for the model, so they
                // come back as a tool error it can act on rather than as a
                // transport failure that ends the turn.
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = exception.Message }]
                };
            }
        });
    })
    // Tool types are registered explicitly rather than by an assembly scan.
    // ExtractionTools stays in the codebase but off the surface while the
    // extraction pipeline is shelved - see Tools/ExtractionTools.cs to
    // restore it.
    .WithTools<DocumentSearchTools>()
    .WithTools<DocumentReadTools>()
    .WithTools<FileAccessTools>()
    .WithTools<OperationsTools>();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// The MCP endpoint is intentionally unauthenticated. Anyone who can reach this
// URL can browse the metadata for, and download, the entire document corpus.
app.MapMcp(McpEndpoint);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "epdesk-mcp"
}));

app.MapGet("/health/ready", async (
    IDbContextFactory<AppDbContext> dbFactory,
    IObjectStorageService storage,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.CanConnectAsync(cancellationToken);
        await storage.CheckConnectionAsync(cancellationToken);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Readiness check failed.");

        return Results.Json(
            new { status = "unready", error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    return Results.Ok(new
    {
        status = "ready",
        database = "ok",
        objectStorage = "ok"
    });
});

app.Run();

/// <summary>
/// Exposed so integration tests can host the server in-process.
/// </summary>
public partial class Program;
