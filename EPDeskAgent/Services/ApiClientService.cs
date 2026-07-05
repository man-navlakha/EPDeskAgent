using System.Net.Http.Json;
using EPDeskAgent.Models;

namespace EPDeskAgent.Services;

public class ApiClientService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClientService> _logger;

    public ApiClientService(
        IConfiguration configuration,
        ILogger<ApiClientService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _httpClient = new HttpClient();

        var apiBaseUrl = _configuration["Agent:ApiBaseUrl"] ?? "";

        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            _httpClient.BaseAddress = new Uri(apiBaseUrl);
        }
    }

    public async Task SendHeartbeatAsync()
    {
        var deviceCode = _configuration["Agent:DeviceCode"] ?? Environment.MachineName;

        var request = new
        {
            deviceCode,
            hostname = Environment.MachineName,
            username = Environment.UserName,
            agentVersion = "1.0.0"
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/agent/heartbeat", request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Heartbeat sent successfully.");
            }
            else
            {
                _logger.LogWarning("Heartbeat failed: {StatusCode}", response.StatusCode);
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

        var deviceCode = _configuration["Agent:DeviceCode"] ?? Environment.MachineName;

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
            var response = await _httpClient.PostAsJsonAsync("/api/agent/files/batch", request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Synced {Count} files to server.", files.Count);
                return true;
            }

            _logger.LogWarning("File sync failed: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not sync files to server.");
            return false;
        }
    }

    public async Task<List<AgentCommand>> GetCommandsAsync()
    {
        var deviceCode = _configuration["Agent:DeviceCode"] ?? Environment.MachineName;

        try
        {
            var url = $"/api/agent/commands?deviceCode={Uri.EscapeDataString(deviceCode)}";

            var commands = await _httpClient.GetFromJsonAsync<List<AgentCommand>>(url);

            return commands ?? new List<AgentCommand>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not get commands from server.");
            return new List<AgentCommand>();
        }
    }

    public async Task<bool> UploadFileAsync(Guid requestId, string filePath)
    {
        if (!File.Exists(filePath))
        {
            await MarkFileRequestFailedAsync(requestId, $"File does not exist on device: {filePath}");
            return false;
        }

        try
        {
            await using var fileStream = File.OpenRead(filePath);

            using var content = new MultipartFormDataContent();

            var fileContent = new StreamContent(fileStream);

            content.Add(
                fileContent,
                "file",
                Path.GetFileName(filePath)
            );

            var response = await _httpClient.PostAsync(
                $"/api/agent/file-request/{requestId}/upload",
                content
            );

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Uploaded requested file: {FilePath}", filePath);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync();

            await MarkFileRequestFailedAsync(
                requestId,
                $"Upload failed. Server status: {response.StatusCode}. Error: {error}"
            );

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not upload requested file.");

            await MarkFileRequestFailedAsync(requestId, ex.Message);

            return false;
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

            await _httpClient.PostAsJsonAsync(
                $"/api/agent/file-request/{requestId}/fail",
                request
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark file request as failed.");
        }
    }
}