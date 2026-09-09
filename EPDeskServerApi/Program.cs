using EPDeskServerApi.Data;
using Microsoft.EntityFrameworkCore;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Services.Storage;
using EPDeskServerApi.Services;
using EPDeskServerApi.Security;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

const long defaultMaxUploadBytes = 2L * 1024 * 1024 * 1024;

var maxUploadBytes = builder.Configuration.GetValue<long?>(
    "UploadLimits:MaxRequestBodySizeBytes"
) ?? defaultMaxUploadBytes;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.Services.Configure<B2StorageOptions>(
    builder.Configuration.GetSection(B2StorageOptions.SectionName)
);
builder.Services.Configure<OldUserDataImportOptions>(
    builder.Configuration.GetSection(OldUserDataImportOptions.SectionName)
);
builder.Services.Configure<DocumentExtractionOptions>(
    builder.Configuration.GetSection(DocumentExtractionOptions.SectionName)
);
builder.Services.Configure<AgentLogCollectionOptions>(
    builder.Configuration.GetSection(AgentLogCollectionOptions.SectionName)
);
builder.Services
    .AddOptions<AgentFileUploadSecurityOptions>()
    .Bind(
        builder.Configuration.GetSection(
            AgentFileUploadSecurityOptions.SectionName
        )
    )
    .Validate(
        options => AgentFileUploadSecurityOptions.HasValidApiKey(
            options.AgentFileUploadApiKey
        ),
        $"Security:AgentFileUploadApiKey is required and must contain at least " +
        $"{AgentFileUploadSecurityOptions.MinimumApiKeyLength} characters without " +
        "leading, trailing, or control characters."
    )
    .ValidateOnStart();

builder.Services.AddSingleton<
    IObjectStorageService,
    B2ObjectStorageService
>();
builder.Services.AddScoped<FileUploadPolicyService>();
builder.Services.AddScoped<DocumentExtractionQueueService>();
builder.Services.AddScoped<AgentFileUploadApiKeyAuthorizationFilter>();
builder.Services.AddHostedService<OldUserDataImportWorker>();
builder.Services.AddHostedService<AgentLogCollectionWorker>();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();

// Lets you confirm which build Railway is actually running without guessing
// from commit dates. Deliberately does not touch the database so a database
// blip cannot take the whole service out of rotation.
var serverVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
var startedAtUtc = DateTime.UtcNow;

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = serverVersion,
    startedAtUtc,
    uptimeSeconds = (long)(DateTime.UtcNow - startedAtUtc).TotalSeconds
}));

app.MapControllers();

app.Run();
