using EPDeskServerApi.Data;
using Microsoft.EntityFrameworkCore;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Services.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<B2StorageOptions>(
    builder.Configuration.GetSection(B2StorageOptions.SectionName)
);

builder.Services.AddSingleton<
    IObjectStorageService,
    B2ObjectStorageService
>();

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

app.MapControllers();

app.Run();