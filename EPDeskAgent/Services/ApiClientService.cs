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
    private string GetDeviceCode()
    {
        var deviceCode = _configuration["Agent:DeviceCode"];

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            deviceCode = Environment.MachineName;
        }

        return deviceCode.Trim().ToUpperInvariant();
    }
    public async Task SendHeartbeatAsync()
    {
        var deviceCode = GetDeviceCode();

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
            var response = await _httpClient.PostAsJsonAsync("/api/agent/files/batch", request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Synced {Count} files to server.", files.Count);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync();

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

        var response = await _httpClient.GetAsync(
            $"/api/agent/commands?deviceCode={Uri.EscapeDataString(deviceCode)}",
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "Failed to get commands. StatusCode: {StatusCode}. Response: {Response}",
                response.StatusCode,
                errorBody
            );

            return new List<AgentCommand>();
        }

        var commands = await response.Content.ReadFromJsonAsync<List<AgentCommand>>(
            cancellationToken: cancellationToken
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

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/agent/file-request/{requestId}/fail",
            payload,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

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

        var response = await _httpClient.PostAsync(
            $"/api/agent/file-request/{requestId}/upload",
            form,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

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