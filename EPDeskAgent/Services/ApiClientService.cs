using System.Net.Http.Json;
using EPDeskAgent.Models;
using System.Reflection;
namespace EPDeskAgent.Services;

public class ApiClientService
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan DefaultUploadTimeout = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClientService> _logger;

    public ApiClientService(
        IConfiguration configuration,
        ILogger<ApiClientService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var apiBaseUrl = _configuration["Agent:ApiBaseUrl"] ?? "";

        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            _httpClient.BaseAddress = new Uri(apiBaseUrl);
        }
    }

    private TimeSpan GetRequestTimeout()
    {
        var seconds = _configuration.GetValue<int>("Agent:HttpRequestTimeoutSeconds");

        if (seconds <= 0)
        {
            return DefaultRequestTimeout;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private TimeSpan GetUploadTimeout()
    {
        var minutes = _configuration.GetValue<int>("Agent:FileUploadTimeoutMinutes");

        if (minutes <= 0)
        {
            return DefaultUploadTimeout;
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private CancellationTokenSource CreateTimeoutTokenSource(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );

        timeoutTokenSource.CancelAfter(timeout);

        return timeoutTokenSource;
    }

    private static string GetCurrentAgentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
    private string GetDeviceCode()
    {
        var deviceCode = _configuration["Agent:DeviceCode"];

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            deviceCode = Environment.MachineName;
        }

        return deviceCode.Trim().ToUpperInvariant();
    }

    public async Task<ScanExclusionsResponse> GetScanExclusionsAsync(
        CancellationToken cancellationToken)
    {
        var deviceCode = GetDeviceCode();

        try
        {
            using var timeoutTokenSource = CreateTimeoutTokenSource(
                GetRequestTimeout(),
                cancellationToken
            );

            var response = await _httpClient.GetAsync(
                $"/api/agent/scan-exclusions?deviceCode={Uri.EscapeDataString(deviceCode)}",
                timeoutTokenSource.Token
            );

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(
                    timeoutTokenSource.Token
                );

                _logger.LogWarning(
                    "Failed to get scan exclusions. StatusCode: {StatusCode}. Response: {Response}",
                    response.StatusCode,
                    errorBody
                );

                return new ScanExclusionsResponse();
            }

            var scanExclusions =
                await response.Content.ReadFromJsonAsync<ScanExclusionsResponse>(
                    cancellationToken: timeoutTokenSource.Token
                );

            return scanExclusions ?? new ScanExclusionsResponse();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get scan exclusions from server.");
            return new ScanExclusionsResponse();
        }
    }
    public async Task UploadLogsAsync(
    Guid? commandId,
    List<AgentLogUploadItem> logs,
    CancellationToken cancellationToken)
    {
        if (logs == null || logs.Count == 0)
        {
            return;
        }

        var payload = new UploadAgentLogsRequest
        {
            DeviceCode = GetDeviceCode(),
            CommandId = commandId,
            Logs = logs
        };

        using var timeoutTokenSource = CreateTimeoutTokenSource(
            GetRequestTimeout(),
            cancellationToken
        );

        var response = await _httpClient.PostAsJsonAsync(
            "/api/agent/logs",
            payload,
            timeoutTokenSource.Token
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            throw new InvalidOperationException(
                $"Log upload failed. StatusCode: {response.StatusCode}. Response: {errorBody}"
            );
        }
    }

    public async Task FailRemoteCommandAsync(
        Guid commandId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty)
        {
            return;
        }

        var payload = new
        {
            errorMessage
        };

        using var timeoutTokenSource = CreateTimeoutTokenSource(
            GetRequestTimeout(),
            cancellationToken
        );

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/agent/remote-command/{commandId}/fail",
            payload,
            timeoutTokenSource.Token
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            _logger.LogWarning(
                "Failed to mark remote command failed. StatusCode: {StatusCode}. Response: {Response}",
                response.StatusCode,
                errorBody
            );
        }
    }
    public async Task SendHeartbeatAsync()
    {
        var deviceCode = GetDeviceCode();

        var request = new
        {
            deviceCode,
            hostname = Environment.MachineName,
            username = Environment.UserName,
            agentVersion = GetCurrentAgentVersion()
        };

        try
        {
            using var timeoutTokenSource = CreateTimeoutTokenSource(
                GetRequestTimeout()
            );

            var response = await _httpClient.PostAsJsonAsync(
                "/api/agent/heartbeat",
                request,
                timeoutTokenSource.Token
            );

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Heartbeat sent successfully. AgentVersion: {AgentVersion}",
                    GetCurrentAgentVersion()
                );
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(
                    timeoutTokenSource.Token
                );

                _logger.LogWarning(
                    "Heartbeat failed. StatusCode: {StatusCode}. Response: {Response}",
                    response.StatusCode,
                    responseBody
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send heartbeat.");
        }
    }

    public async Task<bool> SyncFilesAsync(List<FileMetadata> files)
    {
        if (files.Count == 0)
        {
            return true;
        }

        var deviceCode = GetDeviceCode();

        var request = new
        {
            deviceCode,
            files = files.Select(file => new
            {
                fullPath = file.FullPath,
                directoryPath = file.DirectoryPath,
                fileName = file.FileName,
                extension = file.Extension,
                sizeBytes = file.SizeBytes,
                createdAtUtc = file.CreatedAtUtc,
                updatedAtUtc = file.UpdatedAtUtc,
                isDeleted = file.IsDeleted
            }).ToList()
        };

        try
        {
            using var timeoutTokenSource = CreateTimeoutTokenSource(
                GetRequestTimeout()
            );

            var response = await _httpClient.PostAsJsonAsync(
                "/api/agent/files/batch",
                request,
                timeoutTokenSource.Token
            );

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Synced {Count} files to server.", files.Count);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            _logger.LogWarning(
                "File sync failed: {StatusCode}. Server response: {ErrorBody}",
                response.StatusCode,
                errorBody
            );

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not sync files to server.");
            return false;
        }
    }

    public async Task<List<AgentCommand>> GetCommandsAsync(CancellationToken cancellationToken)
    {
        var deviceCode = GetDeviceCode();

        using var timeoutTokenSource = CreateTimeoutTokenSource(
            GetRequestTimeout(),
            cancellationToken
        );

        var response = await _httpClient.GetAsync(
            $"/api/agent/commands?deviceCode={Uri.EscapeDataString(deviceCode)}",
            timeoutTokenSource.Token
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            _logger.LogWarning(
                "Failed to get commands. StatusCode: {StatusCode}. Response: {Response}",
                response.StatusCode,
                errorBody
            );

            return new List<AgentCommand>();
        }

        var commands = await response.Content.ReadFromJsonAsync<List<AgentCommand>>(
            cancellationToken: timeoutTokenSource.Token
        );

        return commands ?? new List<AgentCommand>();
    }

    public async Task FailFileRequestAsync(
    Guid requestId,
    string errorMessage,
    CancellationToken cancellationToken)
    {
        var payload = new
        {
            errorMessage
        };

        using var timeoutTokenSource = CreateTimeoutTokenSource(
            GetRequestTimeout(),
            cancellationToken
        );

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/agent/file-request/{requestId}/fail",
            payload,
            timeoutTokenSource.Token
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            _logger.LogWarning(
                "Failed to mark file request failed. StatusCode: {StatusCode}. Response: {Response}",
                response.StatusCode,
                errorBody
            );
        }
    }

    public async Task UploadFileAsync(
    Guid requestId,
    string filePath,
    CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        using var form = new MultipartFormDataContent();

        await using var fileStream = File.OpenRead(filePath);

        var fileName = Path.GetFileName(filePath);

        using var fileContent = new StreamContent(fileStream);

        form.Add(fileContent, "file", fileName);

        var uploadTimeout = GetUploadTimeout();

        using var timeoutTokenSource = CreateTimeoutTokenSource(
            uploadTimeout,
            cancellationToken
        );

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.PostAsync(
                $"/api/agent/file-request/{requestId}/upload",
                form,
                timeoutTokenSource.Token
            );
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested &&
                  timeoutTokenSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"File upload timed out after {uploadTimeout.TotalMinutes:0.#} minutes. File: {fileName}",
                exception
            );
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(
                timeoutTokenSource.Token
            );

            throw new InvalidOperationException(
                $"File upload failed. StatusCode: {response.StatusCode}. Response: {errorBody}"
            );
        }
    }

    public async Task MarkFileRequestFailedAsync(Guid requestId, string errorMessage)
    {
        try
        {
            var request = new
            {
                errorMessage
            };

            using var timeoutTokenSource = CreateTimeoutTokenSource(
                GetRequestTimeout()
            );

            await _httpClient.PostAsJsonAsync(
                $"/api/agent/file-request/{requestId}/fail",
                request,
                timeoutTokenSource.Token
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark file request as failed.");
        }
    }
}
