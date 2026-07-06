using EPDeskAgent.Database;
using EPDeskAgent.Scanner;
using EPDeskAgent.Services;

namespace EPDeskAgent;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly FileScanner _fileScanner;
    private readonly FileMetadataRepository _repository;
    private readonly ApiClientService _apiClientService;

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        FileScanner fileScanner,
        FileMetadataRepository repository,
        ApiClientService apiClientService)
    {
        _logger = logger;
        _configuration = configuration;
        _fileScanner = fileScanner;
        _repository = repository;
        _apiClientService = apiClientService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _configuration.GetValue<int>("Agent:ScanIntervalMinutes");

        if (intervalMinutes <= 0)
        {
            intervalMinutes = 1;
        }

        _logger.LogInformation("EPDesk Agent started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _apiClientService.SendHeartbeatAsync();

                _logger.LogInformation("Starting file scan...");

                await _fileScanner.ScanAsync();

                var totalFiles = await _repository.CountFilesAsync();
                var pendingFiles = await _repository.CountPendingFilesAsync();

                _logger.LogInformation(
                    "Scan completed. Total files: {TotalFiles}. Pending sync: {PendingFiles}",
                    totalFiles,
                    pendingFiles
                );

                await SyncPendingFilesAsync();

                await ProcessCommandsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in worker loop.");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task SyncPendingFilesAsync()
    {
        const int batchSize = 100;

        while (true)
        {
            var files = await _repository.GetPendingFilesAsync(batchSize);

            if (files.Count == 0)
            {
                _logger.LogInformation("No pending files to sync.");
                break;
            }

            var success = await _apiClientService.SyncFilesAsync(files);

            if (!success)
            {
                _logger.LogWarning("Stopping sync because server sync failed.");
                break;
            }

            var ids = files.Select(x => x.Id).ToList();

            await _repository.MarkFilesAsSyncedAsync(ids);
        }
    }

    private async Task ProcessCommandsAsync()
    {
        var commands = await _apiClientService.GetCommandsAsync();

        if (commands.Count == 0)
        {
            _logger.LogInformation("No pending commands.");
            return;
        }

        foreach (var command in commands)
        {
            if (command.Type == "UPLOAD_FILE")
            {
                _logger.LogInformation(
                    "Received upload command. RequestId: {RequestId}, File: {FilePath}",
                    command.RequestId,
                    command.FilePath
                );

                await _apiClientService.UploadFileAsync(
                    command.RequestId,
                    command.FilePath
                );
            }
        }
    }
}