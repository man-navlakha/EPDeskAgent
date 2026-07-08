using EPDeskAgent.Database;
using EPDeskAgent.Scanner;
using EPDeskAgent.Services;
using System.Net.Http.Json;

namespace EPDeskAgent;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly FileScanner _fileScanner;
    private readonly FileMetadataRepository _repository;
    private readonly ApiClientService _apiClientService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly ZipService _zipService;


    public Worker(
     ILogger<Worker> logger,
     IConfiguration configuration,
     FileScanner fileScanner,
     FileMetadataRepository repository,
     ApiClientService apiClientService,
     AutoUpdateService autoUpdateService,
     ZipService zipService)
    {
        _logger = logger;
        _configuration = configuration;
        _fileScanner = fileScanner;
        _repository = repository;
        _apiClientService = apiClientService;
        _autoUpdateService = autoUpdateService;
        _zipService = zipService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EPDesk Agent started.");

        var heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        var scanTask = RunScanLoopAsync(stoppingToken);
        var commandTask = RunCommandLoopAsync(stoppingToken);
        var updateTask = RunUpdateLoopAsync(stoppingToken);

        await Task.WhenAll(heartbeatTask, scanTask, commandTask, updateTask);
    }
    private async Task RunUpdateLoopAsync(CancellationToken stoppingToken)
    {
        var updateCheckMinutes = _configuration.GetValue<int>("Agent:UpdateCheckIntervalMinutes");

        if (updateCheckMinutes <= 0)
        {
            updateCheckMinutes = 60;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _autoUpdateService.CheckAndUpdateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto update loop error.");
            }

            await Task.Delay(TimeSpan.FromMinutes(updateCheckMinutes), stoppingToken);
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        var heartbeatSeconds = _configuration.GetValue<int>("Agent:HeartbeatIntervalSeconds");

        if (heartbeatSeconds <= 0)
        {
            heartbeatSeconds = 60;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _apiClientService.SendHeartbeatAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), stoppingToken);
        }
    }

    private async Task RunScanLoopAsync(CancellationToken stoppingToken)
    {
        var scanIntervalMinutes = _configuration.GetValue<int>("Agent:ScanIntervalMinutes");

        if (scanIntervalMinutes <= 0)
        {
            scanIntervalMinutes = 360;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File scan loop error.");
            }

            await Task.Delay(TimeSpan.FromMinutes(scanIntervalMinutes), stoppingToken);
        }
    }

    private async Task RunCommandLoopAsync(CancellationToken stoppingToken)
    {
        var commandPollSeconds = _configuration.GetValue<int>("Agent:CommandPollIntervalSeconds");

        if (commandPollSeconds <= 0)
        {
            commandPollSeconds = 30;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessCommandsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(commandPollSeconds), stoppingToken);
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

    

    private async Task ProcessCommandsAsync(CancellationToken stoppingToken)
    {
        var commands = await _apiClientService.GetCommandsAsync(stoppingToken);

        foreach (var command in commands)
        {
            try
            {
                if (command.Type == "UPLOAD_FILE")
                {
                    if (!File.Exists(command.FilePath))
                    {
                        await _apiClientService.FailFileRequestAsync(
                            command.RequestId,
                            $"File not found: {command.FilePath}",
                            stoppingToken
                        );

                        continue;
                    }

                    await _apiClientService.UploadFileAsync(
                        command.RequestId,
                        command.FilePath,
                        stoppingToken
                    );
                }
                else if (command.Type == "UPLOAD_ZIP")
                {
                    string zipPath;

                    if (command.RequestType == "folder_zip")
                    {
                        zipPath = _zipService.CreateZipFromFolder(
                            command.FolderPath,
                            command.RequestId
                        );
                    }
                    else if (command.RequestType == "multiple_files_zip")
                    {
                        zipPath = _zipService.CreateZipFromFiles(
                            command.Paths,
                            command.RequestId
                        );
                    }
                    else
                    {
                        await _apiClientService.FailFileRequestAsync(
                            command.RequestId,
                            $"Unknown ZIP request type: {command.RequestType}",
                            stoppingToken
                        );

                        continue;
                    }

                    await _apiClientService.UploadFileAsync(
                        command.RequestId,
                        zipPath,
                        stoppingToken
                    );

                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch
                    {
                        // Ignore cleanup failure
                    }
                }
                else
                {
                    await _apiClientService.FailFileRequestAsync(
                        command.RequestId,
                        $"Unknown command type: {command.Type}",
                        stoppingToken
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Command failed. RequestId: {RequestId}",
                    command.RequestId
                );

                await _apiClientService.FailFileRequestAsync(
                    command.RequestId,
                    ex.Message,
                    stoppingToken
                );
            }
        }
    }
}