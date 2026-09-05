using Amazon.Runtime;
using Amazon.S3;
using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Health;
using EPDeskExtractionWorker.Services;
using EPDeskExtractionWorker.Services.Jobs;
using EPDeskExtractionWorker.Services.Processing;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services
    .AddOptions<DatabaseOptions>()
    .Configure(options =>
    {
        options.ConnectionString =
            builder.Configuration.GetConnectionString("DefaultConnection") ?? "";
    })
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "ConnectionStrings:DefaultConnection is required."
    )
    .ValidateOnStart();

builder.Services
    .AddOptions<B2StorageOptions>()
    .Bind(builder.Configuration.GetSection(B2StorageOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(
            options.ServiceUrl,
            UriKind.Absolute,
            out var serviceUri
        ) && serviceUri.Scheme == Uri.UriSchemeHttps,
        "B2:ServiceUrl must be a valid HTTPS endpoint."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Region),
        "B2:Region is required."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.BucketName),
        "B2:BucketName is required."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.KeyId),
        "B2:KeyId is required."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ApplicationKey),
        "B2:ApplicationKey is required."
    )
    .ValidateOnStart();

builder.Services
    .AddOptions<ExtractionWorkerOptions>()
    .Bind(builder.Configuration.GetSection(ExtractionWorkerOptions.SectionName))
    .Validate(
        options => options.PollIntervalSeconds > 0,
        "ExtractionWorker:PollIntervalSeconds must be greater than zero."
    )
    .Validate(
        options => options.MaxConcurrentJobs > 0,
        "ExtractionWorker:MaxConcurrentJobs must be greater than zero."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.PipelineVersion) &&
                   options.PipelineVersion.Length <= 64,
        "ExtractionWorker:PipelineVersion is required and cannot exceed 64 characters."
    )
    .Validate(
        options => options.LeaseSeconds >= 30 &&
                   options.HeartbeatSeconds > 0 &&
                   options.HeartbeatSeconds * 2 < options.LeaseSeconds,
        "The heartbeat interval must be less than half of the lease duration."
    )
    .Validate(
        options => options.ProcessingTimeoutSeconds > 0 &&
                   options.RetryBaseSeconds > 0,
        "Processing and retry timeouts must be greater than zero."
    )
    .Validate(
        options => options.MaxDownloadBytes > 0 &&
                   options.MaxJobsPerInstanceLifetime >= 0,
        "Download and lifetime job limits are invalid."
    )
    .Validate(
        options => string.IsNullOrWhiteSpace(options.CanaryJobId) ||
                   (Guid.TryParse(options.CanaryJobId, out var jobId) && jobId != Guid.Empty),
        "ExtractionWorker:CanaryJobId must be an empty value or a non-empty GUID."
    )
    .Validate(
        options => !options.ProcessingEnabled ||
                   options.MaxJobsPerInstanceLifetime != 1 ||
                   !string.IsNullOrWhiteSpace(options.CanaryJobId),
        "An explicit CanaryJobId is required for one-job production processing."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.TempRoot),
        "ExtractionWorker:TempRoot is required."
    )
    .Validate(
        options => options.AllowedExtensions.Length > 0 &&
                   options.AllowedExtensions.All(extension =>
                       !string.IsNullOrWhiteSpace(extension) &&
                       extension.StartsWith('.') &&
                       extension.Length <= 32),
        "At least one valid ExtractionWorker:AllowedExtensions entry is required."
    )
    .Validate(
        options => options.MaxSandboxResponseBytes is > 0 and <= int.MaxValue,
        "ExtractionWorker:MaxSandboxResponseBytes must be between 1 and 2147483647 bytes."
    )
    .Validate(
        options => !options.ProcessingEnabled ||
                   SandboxEndpointPolicy.TryCreateSafeBaseUri(
                       options.SandboxBaseUrl,
                       out _),
        "ExtractionWorker:SandboxBaseUrl must use HTTPS, Railway private HTTP, or loopback HTTP while processing is enabled."
    )
    .Validate(
        options => !options.ProcessingEnabled ||
                   SandboxEndpointPolicy.IsValidApiKey(options.SandboxApiKey),
        "ExtractionWorker:SandboxApiKey must be a 32-1024 character secret without whitespace while processing is enabled."
    )
    .ValidateOnStart();

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<DatabaseOptions>>()
        .Value;

    return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
});

builder.Services.AddSingleton<IAmazonS3>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<B2StorageOptions>>()
        .Value;

    var credentials = new BasicAWSCredentials(
        options.KeyId,
        options.ApplicationKey
    );

    var configuration = new AmazonS3Config
    {
        ServiceURL = options.ServiceUrl.TrimEnd('/'),
        AuthenticationRegion = options.Region,
        ForcePathStyle = true
    };

    return new AmazonS3Client(credentials, configuration);
});

builder.Services
    .AddHealthChecks()
    .AddCheck<PostgresHealthCheck>(
        "postgres",
        tags: ["ready"]
    )
    .AddCheck<B2HealthCheck>(
        "backblaze-b2",
        tags: ["ready"]
    )
    .AddCheck<SandboxHealthCheck>(
        "extraction-sandbox",
        tags: ["ready"]
    );

builder.Services.AddSingleton<IExtractionJobStore, NpgsqlExtractionJobStore>();
builder.Services.AddSingleton<TemporaryWorkspaceFactory>();
builder.Services.AddSingleton<B2ObjectTransferService>();
builder.Services.AddSingleton<ExtractionArtifactBuilder>();
builder.Services.AddSingleton<ExtractionWorkerRuntimeState>();
builder.Services
    .AddHttpClient<SandboxExtractionClient>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 64
    });
builder.Services.AddHostedService<ExtractionWorker>();

var app = builder.Build();

app.MapGet(
    "/",
    (
        IOptions<ExtractionWorkerOptions> options,
        ExtractionWorkerRuntimeState runtimeState
    ) => Results.Ok(new
    {
        service = "epdesk-extraction-worker",
        status = options.Value.ProcessingEnabled
            ? "processing-v1-canary"
            : "processing-disabled",
        processingEnabled = options.Value.ProcessingEnabled,
        pipelineVersion = options.Value.PipelineVersion,
        maxConcurrentJobs = options.Value.MaxConcurrentJobs,
        maxJobsPerInstanceLifetime = options.Value.MaxJobsPerInstanceLifetime,
        runtime = runtimeState.Snapshot()
    })
);

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = _ => false
    }
);

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready")
    }
);

app.Run();
