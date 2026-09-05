using EPDeskAgent;
using EPDeskAgent.Database;
using EPDeskAgent.Models;
using EPDeskAgent.Scanner;
using EPDeskAgent.Services;


// Windows services and manually launched published executables can have a
// working directory that is different from the executable directory. Always
// load appsettings.json from beside EPDeskAgent.exe.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

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

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EPDesk Agent";
});

builder.Services.AddSingleton<LocalDatabase>();
builder.Services.AddSingleton<FileMetadataRepository>();
builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<ApiClientService>();
builder.Services.AddSingleton<AutoUpdateService>();
builder.Services.AddSingleton<AgentRemovalService>();
builder.Services.AddSingleton<AgentActivityControl>();
builder.Services.AddSingleton<ZipService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<LocalLogService>();
builder.Services.AddSingleton<AutomaticFileUploadService>();


var host = builder.Build();

var database = host.Services.GetRequiredService<LocalDatabase>();
database.Initialize();

host.Run();
