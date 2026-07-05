using EPDeskAgent;
using EPDeskAgent.Database;
using EPDeskAgent.Scanner;
using EPDeskAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EPDesk Agent";
});

builder.Services.AddSingleton<LocalDatabase>();
builder.Services.AddSingleton<FileMetadataRepository>();
builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<ApiClientService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

var database = host.Services.GetRequiredService<LocalDatabase>();
database.Initialize();

host.Run();