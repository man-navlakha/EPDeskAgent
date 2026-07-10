using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using EPDeskAgent.Models;
using Microsoft.Extensions.Hosting;

namespace EPDeskAgent.Services;

public class AutoUpdateService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoUpdateService> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly HttpClient _httpClient;

    public AutoUpdateService(
        IConfiguration configuration,
        ILogger<AutoUpdateService> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _configuration = configuration;
        _logger = logger;
        _applicationLifetime = applicationLifetime;

        _httpClient = new HttpClient();

        var apiBaseUrl = _configuration["Agent:ApiBaseUrl"] ?? "";

        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            _httpClient.BaseAddress = new Uri(apiBaseUrl);
        }
    }

    public async Task CheckAndUpdateAsync(CancellationToken cancellationToken)
    {
        var autoUpdateEnabled = _configuration.GetValue<bool>("Agent:AutoUpdateEnabled");

        if (!autoUpdateEnabled)
        {
            _logger.LogInformation("Auto update is disabled.");
            return;
        }

        var currentVersion = GetCurrentVersion();
        var deviceCode = GetDeviceCode();

        _logger.LogInformation(
            "Checking update. CurrentVersion: {CurrentVersion}, DeviceCode: {DeviceCode}",
            currentVersion,
            deviceCode
        );

        var updateInfo = await GetLatestUpdateAsync(
            currentVersion,
            deviceCode,
            cancellationToken
        );

        if (updateInfo == null)
        {
            _logger.LogWarning("Update API returned empty response.");
            return;
        }

        if (!updateInfo.UpdateAvailable)
        {
            _logger.LogInformation("No update available.");
            return;
        }

        if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
        {
            _logger.LogWarning("Update available but download URL is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(updateInfo.Sha256))
        {
            _logger.LogWarning("Update available but SHA256 is empty. Update stopped.");
            return;
        }

        _logger.LogInformation(
            "Update available. LatestVersion: {LatestVersion}",
            updateInfo.LatestVersion
        );

        var msiPath = await DownloadMsiAsync(
            updateInfo,
            cancellationToken
        );

        var hashOk = await VerifySha256Async(
            msiPath,
            updateInfo.Sha256,
            cancellationToken
        );

        if (!hashOk)
        {
            _logger.LogError("MSI SHA256 verification failed. Update stopped.");
            return;
        }

        _logger.LogInformation("MSI verified successfully. Starting silent installation.");

        StartMsiInstall(msiPath);

        _logger.LogInformation("Stopping current Agent so MSI can upgrade service.");

        _applicationLifetime.StopApplication();
    }

    private async Task<AgentUpdateInfo?> GetLatestUpdateAsync(
        string currentVersion,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var url =
            $"/api/agent/update/latest?currentVersion={Uri.EscapeDataString(currentVersion)}&deviceCode={Uri.EscapeDataString(deviceCode)}";

        return await _httpClient.GetFromJsonAsync<AgentUpdateInfo>(
            url,
            cancellationToken
        );
    }

    private async Task<string> DownloadMsiAsync(
        AgentUpdateInfo updateInfo,
        CancellationToken cancellationToken)
    {
        var updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EPDeskAgent",
            "Updates"
        );

        Directory.CreateDirectory(updateDir);

        var fileName = $"EPDeskAgentSetup-{updateInfo.LatestVersion}.msi";
        var msiPath = Path.Combine(updateDir, fileName);

        _logger.LogInformation("Downloading MSI from {Url}", updateInfo.DownloadUrl);

        using var response = await _httpClient.GetAsync(
            updateInfo.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = new FileStream(msiPath, FileMode.Create, FileAccess.Write, FileShare.None);

        await inputStream.CopyToAsync(outputStream, cancellationToken);

        _logger.LogInformation("Downloaded MSI to {MsiPath}", msiPath);

        return msiPath;
    }

    private async Task<bool> VerifySha256Async(
    string filePath,
    string expectedSha256,
    CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);

        using var sha256 = SHA256.Create();

        var hashBytes = await sha256.ComputeHashAsync(
            stream,
            cancellationToken
        );

        var actualSha256 = Convert
            .ToHexString(hashBytes)
            .ToLowerInvariant();

        var expected = NormalizeSha256(expectedSha256);

        if (!IsValidSha256(expected))
        {
            _logger.LogError(
                "The configured SHA256 is invalid. Value: {ExpectedSha256}",
                expectedSha256
            );

            return false;
        }

        _logger.LogInformation(
            "Expected SHA256: {Expected}",
            expected
        );

        _logger.LogInformation(
            "Actual SHA256: {Actual}",
            actualSha256
        );

        return string.Equals(
            actualSha256,
            expected,
            StringComparison.OrdinalIgnoreCase
        );
    }
    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();

        if (normalized.StartsWith(
            "sha256:",
            StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["sha256:".Length..];
        }

        normalized = normalized
            .Replace(" ", "")
            .Replace("-", "")
            .Trim()
            .ToLowerInvariant();

        return normalized;
    }
    private static bool IsValidSha256(string value)
    {
        return value.Length == 64 &&
               value.All(Uri.IsHexDigit);
    }
    private void StartMsiInstall(string msiPath)
    {
        var arguments = $"/i \"{msiPath}\" /qn /norestart";

        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(startInfo);
    }

    private string GetCurrentVersion()
    {
        var informationalVersion = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+')[0];
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
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
}