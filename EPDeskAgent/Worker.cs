using EPDeskAgent.Database;
using EPDeskAgent.Scanner;
using EPDeskAgent.Services;
using EPDeskAgent.Models;
using System.Diagnostics;
using System.Text.Json;

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
    private readonly LocalLogService _localLogService;
    private readonly AutomaticFileUploadService _automaticFileUploadService;

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        FileScanner fileScanner,
        FileMetadataRepository repository,
        ApiClientService apiClientService,
        AutoUpdateService autoUpdateService,
        ZipService zipService,
        LocalLogService localLogService,
        AutomaticFileUploadService automaticFileUploadService)
    {
        _logger = logger;
        _configuration = configuration;
        _fileScanner = fileScanner;
        _repository = repository;
        _apiClientService = apiClientService;
        _autoUpdateService = autoUpdateService;
        _zipService = zipService;
        _localLogService = localLogService;
        _automaticFileUploadService = automaticFileUploadService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConfigureProcessPriority();

        _logger.LogInformation("EPDesk Agent started.");

        _localLogService.Info(
            "agent",
            "EPDesk Agent service started.",
            step: "service_started"
        );

        var heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        var scanTask = RunScanLoopAsync(stoppingToken);
        var commandTask = RunCommandLoopAsync(stoppingToken);
        var updateTask = RunUpdateLoopAsync(stoppingToken);
        var automaticUploadTask = RunAutomaticUploadLoopAsync(stoppingToken);

        await Task.WhenAll(
            heartbeatTask,
            scanTask,
            commandTask,
            updateTask,
            automaticUploadTask
        );
    }

    private async Task RunAutomaticUploadLoopAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _configuration.GetValue<int>(
            "Agent:AutomaticUploadIntervalMinutes"
        );

        if (intervalMinutes <= 0)
        {
            intervalMinutes = 10;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _automaticFileUploadService.UploadChangedFilesAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Automatic Backblaze upload loop error.");

                _localLogService.Error(
                    "upload",
                    "Automatic Backblaze upload loop error.",
                    exception,
                    step: "automatic_upload_loop_failed"
                );
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
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
                _localLogService.Info(
                    "agent",
                    "Checking for Agent update.",
                    step: "update_check_started"
                );

                await _autoUpdateService.CheckAndUpdateAsync(stoppingToken);

                _localLogService.Info(
                    "agent",
                    "Agent update check completed.",
                    step: "update_check_completed"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto update loop error.");

                _localLogService.Error(
                    "agent",
                    "Auto update loop error.",
                    ex,
                    step: "update_check_failed"
                );
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

                _localLogService.Error(
                    "agent",
                    "Heartbeat loop error.",
                    ex,
                    step: "heartbeat_failed"
                );
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

        var initialScanDelayMinutes = _configuration.GetValue<int>("Agent:InitialScanDelayMinutes");

        if (initialScanDelayMinutes > 0)
        {
            _logger.LogInformation(
                "Waiting {Minutes} minutes before first file scan.",
                initialScanDelayMinutes
            );

            _localLogService.Info(
                "agent",
                $"Waiting {initialScanDelayMinutes} minutes before first file scan.",
                step: "initial_scan_delay"
            );

            await Task.Delay(TimeSpan.FromMinutes(initialScanDelayMinutes), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting file scan.");

                _localLogService.Info(
                    "agent",
                    "File scan started.",
                    step: "scan_started"
                );

                var scanExclusions = await _apiClientService
                    .GetScanExclusionsAsync(stoppingToken);

                _localLogService.Info(
                    "agent",
                    $"Server scan exclusions loaded. FolderPaths: {scanExclusions.ExcludedFolders.Count}. FolderNames: {scanExclusions.ExcludedFolderNames.Count}. FileExtensions: {scanExclusions.ExcludedFileExtensions.Count}.",
                    step: "scan_exclusions_loaded"
                );

                await _fileScanner.ScanAsync(scanExclusions, stoppingToken);

                var totalFiles = await _repository.CountFilesAsync();
                var pendingFiles = await _repository.CountPendingFilesAsync();

                _logger.LogInformation(
                    "Scan completed. Total files: {TotalFiles}. Pending sync: {PendingFiles}",
                    totalFiles,
                    pendingFiles
                );

                _localLogService.Info(
                    "agent",
                    $"File scan completed. Total files: {totalFiles}. Pending sync: {pendingFiles}.",
                    step: "scan_completed"
                );

                await SyncPendingFilesAsync(stoppingToken);

                await _automaticFileUploadService.UploadChangedFilesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File scan loop error.");

                _localLogService.Error(
                    "agent",
                    "File scan loop error.",
                    ex,
                    step: "scan_failed"
                );
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

                _localLogService.Error(
                    "agent",
                    "Command loop error.",
                    ex,
                    step: "command_loop_failed"
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(commandPollSeconds), stoppingToken);
        }
    }

    private async Task SyncPendingFilesAsync(CancellationToken stoppingToken)
    {
        var batchSize = _configuration.GetValue<int>("Agent:MetadataSyncBatchSize");
        if (batchSize <= 0)
        {
            batchSize = 100;
        }

        var pauseMilliseconds = _configuration.GetValue<int>("Agent:MetadataSyncPauseMilliseconds");

        while (!stoppingToken.IsCancellationRequested)
        {
            var files = await _repository.GetPendingFilesAsync(batchSize);

            if (files.Count == 0)
            {
                _logger.LogInformation("No pending files to sync.");

                _localLogService.Info(
                    "agent",
                    "No pending files to sync.",
                    step: "sync_no_pending_files"
                );

                break;
            }

            _localLogService.Info(
                "agent",
                $"Syncing file metadata batch. Count: {files.Count}.",
                step: "metadata_sync_started"
            );

            var success = await _apiClientService.SyncFilesAsync(files);

            if (!success)
            {
                _logger.LogWarning("Stopping sync because server sync failed.");

                _localLogService.Warning(
                    "agent",
                    "Stopping sync because server sync failed.",
                    step: "metadata_sync_failed"
                );

                break;
            }

            var ids = files.Select(x => x.Id).ToList();

            await _repository.MarkFilesAsSyncedAsync(ids);

            _localLogService.Info(
                "agent",
                $"File metadata sync completed. Count: {files.Count}.",
                step: "metadata_sync_completed"
            );

            if (pauseMilliseconds > 0)
            {
                await Task.Delay(pauseMilliseconds, stoppingToken);
            }
        }
    }

    private void ConfigureProcessPriority()
    {
        var runBelowNormalPriority = _configuration.GetValue<bool>("Agent:RunBelowNormalPriority");

        if (!runBelowNormalPriority)
        {
            return;
        }

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;

            _logger.LogInformation("Agent process priority set to below normal.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set Agent process priority.");
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken stoppingToken)
    {
        var commands = await _apiClientService.GetCommandsAsync(stoppingToken);

        if (commands.Count == 0)
        {
            return;
        }

        _localLogService.Info(
            "agent",
            $"Commands received from server. Count: {commands.Count}.",
            step: "commands_received"
        );

        foreach (var command in commands)
        {
            try
            {
                if (command.Type == "UPLOAD_FILE")
                {
                    _localLogService.Info(
                        "file-request",
                        $"File upload command received. Path: {command.FilePath}",
                        command.RequestId,
                        "request_received"
                    );

                    if (!File.Exists(command.FilePath))
                    {
                        var message = $"File not found: {command.FilePath}";

                        _localLogService.Warning(
                            "file-request",
                            message,
                            command.RequestId,
                            "file_missing"
                        );

                        await _apiClientService.FailFileRequestAsync(
                            command.RequestId,
                            message,
                            stoppingToken
                        );

                        continue;
                    }

                    _localLogService.Info(
                        "upload",
                        $"Upload started: {command.FilePath}",
                        command.RequestId,
                        "upload_started"
                    );

                    await _apiClientService.UploadFileAsync(
                        command.RequestId,
                        command.FilePath,
                        stoppingToken
                    );

                    _localLogService.Info(
                        "upload",
                        "Upload completed successfully.",
                        command.RequestId,
                        "upload_completed"
                    );
                }
                else if (command.Type == "UPLOAD_ZIP")
                {
                    _localLogService.Info(
                        "file-request",
                        $"ZIP command received. RequestType: {command.RequestType}",
                        command.RequestId,
                        "zip_request_received"
                    );

                    string zipPath;

                    if (command.RequestType == "folder_zip")
                    {
                        _localLogService.Info(
                            "file-request",
                            $"Folder ZIP started: {command.FolderPath}",
                            command.RequestId,
                            "zipping_started"
                        );

                        zipPath = _zipService.CreateZipFromFolder(
                            command.FolderPath,
                            command.RequestId
                        );
                    }
                    else if (command.RequestType == "multiple_files_zip")
                    {
                        _localLogService.Info(
                            "file-request",
                            $"Multiple files ZIP started. File count: {command.Paths.Count}.",
                            command.RequestId,
                            "zipping_started"
                        );

                        zipPath = _zipService.CreateZipFromFiles(
                            command.Paths,
                            command.RequestId
                        );
                    }
                    else
                    {
                        var message = $"Unknown ZIP request type: {command.RequestType}";

                        _localLogService.Warning(
                            "file-request",
                            message,
                            command.RequestId,
                            "unknown_zip_request_type"
                        );

                        await _apiClientService.FailFileRequestAsync(
                            command.RequestId,
                            message,
                            stoppingToken
                        );

                        continue;
                    }

                    var zipInfo = new FileInfo(zipPath);

                    _localLogService.Info(
                        "file-request",
                        $"ZIP created: {zipPath}. Size: {zipInfo.Length} bytes.",
                        command.RequestId,
                        "zipping_completed"
                    );

                    _localLogService.Info(
                        "upload",
                        $"ZIP upload started: {zipPath}",
                        command.RequestId,
                        "upload_started"
                    );

                    await _apiClientService.UploadFileAsync(
                        command.RequestId,
                        zipPath,
                        stoppingToken
                    );

                    _localLogService.Info(
                        "upload",
                        "ZIP upload completed successfully.",
                        command.RequestId,
                        "upload_completed"
                    );

                    try
                    {
                        File.Delete(zipPath);

                        _localLogService.Info(
                            "file-request",
                            $"Temporary ZIP deleted: {zipPath}",
                            command.RequestId,
                            "temp_zip_deleted"
                        );
                    }
                    catch (Exception ex)
                    {
                        _localLogService.Warning(
                            "file-request",
                            $"Failed to delete temporary ZIP: {zipPath}. Error: {ex.Message}",
                            command.RequestId,
                            "temp_zip_cleanup_failed"
                        );
                    }
                }
                else if (command.Type == "REQUEST_LOGS")
                {
                    _localLogService.Info(
                        "diagnostic",
                        "REQUEST_LOGS command received.",
                        step: "request_logs_received"
                    );

                    try
                    {
                        var payload = new RequestLogsPayload();

                        if (!string.IsNullOrWhiteSpace(command.PayloadJson))
                        {
                            payload = JsonSerializer.Deserialize<RequestLogsPayload>(
                                command.PayloadJson,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                }
                            ) ?? new RequestLogsPayload();
                        }

                        if (payload.TakeLines <= 0)
                        {
                            payload.TakeLines = 500;
                        }

                        if (payload.TakeLines > 5000)
                        {
                            payload.TakeLines = 5000;
                        }

                        if (string.IsNullOrWhiteSpace(payload.LogType))
                        {
                            payload.LogType = "all";
                        }

                        var lines = _localLogService.ReadRecentLines(
                            payload.LogType,
                            payload.TakeLines
                        );

                        if (lines.Count == 0)
                        {
                            lines.Add("No local logs found on device.");
                        }

                        var logItems = lines.Select(line => new AgentLogUploadItem
                        {
                            RequestId = null,
                            Level = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                                ? "ERROR"
                                : line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                                    ? "WARNING"
                                    : "INFO",
                            Category = "diagnostic",
                            Step = "fresh_logs_uploaded",
                            Message = line,
                            DetailsJson = "",
                            CreatedAtUtc = DateTime.UtcNow
                        }).ToList();

                        await _apiClientService.UploadLogsAsync(
                            command.CommandId,
                            logItems,
                            stoppingToken
                        );

                        _localLogService.Info(
                            "diagnostic",
                            $"Fresh logs uploaded successfully. Lines: {logItems.Count}.",
                            step: "request_logs_uploaded"
                        );
                    }
                    catch (Exception ex)
                    {
                        _localLogService.Error(
                            "diagnostic",
                            "REQUEST_LOGS command failed.",
                            ex,
                            step: "request_logs_failed"
                        );

                        await _apiClientService.FailRemoteCommandAsync(
                            command.CommandId,
                            ex.Message,
                            stoppingToken
                        );
                    }

                    continue;
                }
                else if (command.Type == "RUN_DIAGNOSTICS")
                {
                    _localLogService.Info(
                        "diagnostic",
                        "RUN_DIAGNOSTICS command received. Agent-side diagnostics implementation pending.",
                        step: "run_diagnostics_received"
                    );

                    // Next step: we will implement diagnostics collection and upload to /api/agent/diagnostics.
                    continue;
                }
                else
                {
                    _localLogService.Warning(
                        "agent",
                        $"Unknown command type received: {command.Type}",
                        step: "unknown_command_type"
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

                _localLogService.Error(
                    "file-request",
                    "Command failed.",
                    ex,
                    command.RequestId,
                    "failed"
                );

                if (command.RequestId != Guid.Empty)
                {
                    await _apiClientService.FailFileRequestAsync(
                        command.RequestId,
                        ex.Message,
                        stoppingToken
                    );
                }
            }
        }
    }
}
