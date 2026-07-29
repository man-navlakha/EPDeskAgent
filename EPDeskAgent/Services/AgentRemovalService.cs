using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace EPDeskAgent.Services;

public sealed partial class AgentRemovalService
{
    private const string TargetDisplayName = "EPDesk Agent";
    private const string UninstallRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<AgentRemovalService> _logger;

    public AgentRemovalService(
        IConfiguration configuration,
        IHostApplicationLifetime applicationLifetime,
        ILogger<AgentRemovalService> logger)
    {
        _configuration = configuration;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    public int ScheduleRemoval(Guid commandId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "EPDesk Agent removal is supported only on Windows."
            );
        }

        if (commandId == Guid.Empty)
        {
            throw new ArgumentException(
                "A remote command ID is required.",
                nameof(commandId)
            );
        }

        var productCodes = FindMatchingMsiProductCodes();

        if (productCodes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No MSI installation named exactly '{TargetDisplayName}' was found."
            );
        }

        var callbackBaseUrl = _configuration["Agent:ApiBaseUrl"];

        if (!Uri.TryCreate(callbackBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp &&
             baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Agent:ApiBaseUrl must be a valid HTTP or HTTPS URL."
            );
        }

        var completeUri = new Uri(
            baseUri,
            $"/api/agent/remote-command/{commandId}/complete"
        );
        var failUri = new Uri(
            baseUri,
            $"/api/agent/remote-command/{commandId}/fail"
        );
        var deviceCode = GetDeviceCode();
        var helperPath = Path.Combine(
            Path.GetTempPath(),
            $"EPDeskAgentRemoval-{commandId:N}.ps1"
        );

        var script = BuildRemovalScript(
            Environment.ProcessId,
            productCodes,
            completeUri,
            failUri,
            deviceCode
        );

        File.WriteAllText(
            helperPath,
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(helperPath);

        using var helperProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows could not start the Agent removal helper."
            );

        _logger.LogWarning(
            "Removal helper started. ProductCount: {ProductCount}, HelperProcessId: {HelperProcessId}",
            productCodes.Count,
            helperProcess.Id
        );

        _applicationLifetime.StopApplication();

        return productCodes.Count;
    }

    [SupportedOSPlatform("windows")]
    private List<string> FindMatchingMsiProductCodes()
    {
        var productCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var registryViews = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Registry32 };

        foreach (var registryView in registryViews)
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                registryView
            );
            using var uninstallKey = localMachine.OpenSubKey(
                UninstallRegistryPath,
                writable: false
            );

            if (uninstallKey == null)
            {
                continue;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var productKey = uninstallKey.OpenSubKey(
                    subKeyName,
                    writable: false
                );

                var displayName = productKey?.GetValue("DisplayName") as string;

                if (!string.Equals(
                        displayName?.Trim(),
                        TargetDisplayName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var productCode = ExtractMsiProductCode(
                    subKeyName,
                    productKey?.GetValue("UninstallString") as string
                );

                if (productCode != null)
                {
                    productCodes.Add(productCode);
                }
                else
                {
                    _logger.LogWarning(
                        "Found '{DisplayName}' in the {RegistryView} uninstall registry, but it is not an MSI product.",
                        TargetDisplayName,
                        registryView
                    );
                }
            }
        }

        return productCodes.ToList();
    }

    private static string? ExtractMsiProductCode(
        string subKeyName,
        string? uninstallString)
    {
        if (Guid.TryParse(subKeyName, out var productGuid))
        {
            return productGuid.ToString("B").ToUpperInvariant();
        }

        if (string.IsNullOrWhiteSpace(uninstallString) ||
            !uninstallString.Contains(
                "msiexec",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = ProductCodeRegex().Match(uninstallString);

        return match.Success && Guid.TryParse(match.Value, out productGuid)
            ? productGuid.ToString("B").ToUpperInvariant()
            : null;
    }

    private static string BuildRemovalScript(
        int agentProcessId,
        IReadOnlyCollection<string> productCodes,
        Uri completeUri,
        Uri failUri,
        string deviceCode)
    {
        var productCodeList = string.Join(
            ", ",
            productCodes.Select(value => $"'{EscapePowerShell(value)}'")
        );

        return $$"""
            $ErrorActionPreference = 'Stop'
            $productCodes = @({{productCodeList}})
            $successExitCodes = @(0, 1605, 1614, 1641, 3010)

            try {
                for ($attempt = 0; $attempt -lt 60; $attempt++) {
                    if (-not (Get-Process -Id {{agentProcessId}} -ErrorAction SilentlyContinue)) {
                        break
                    }

                    Start-Sleep -Seconds 1
                }

                Start-Sleep -Seconds 2

                $results = foreach ($productCode in $productCodes) {
                    $installAttempt = 0

                    do {
                        $installAttempt++
                        $process = Start-Process `
                            -FilePath "$env:SystemRoot\System32\msiexec.exe" `
                            -ArgumentList @('/x', $productCode, '/qn', '/norestart') `
                            -Wait `
                            -PassThru

                        if ($process.ExitCode -eq 1618 -and $installAttempt -lt 40) {
                            Start-Sleep -Seconds 15
                        }
                    } while ($process.ExitCode -eq 1618 -and $installAttempt -lt 40)

                    if ($process.ExitCode -notin $successExitCodes) {
                        throw "MSI uninstall failed for $productCode with exit code $($process.ExitCode)."
                    }

                    "$productCode=$($process.ExitCode)"
                }

                $message = "Removed {{TargetDisplayName}} MSI installation(s): $($results -join ', ')"
                $body = @{
                    deviceCode = '{{EscapePowerShell(deviceCode)}}'
                    message = $message
                } | ConvertTo-Json

                Invoke-RestMethod `
                    -Method Post `
                    -Uri '{{EscapePowerShell(completeUri.AbsoluteUri)}}' `
                    -ContentType 'application/json' `
                    -Body $body | Out-Null
            }
            catch {
                try {
                    $body = @{
                        errorMessage = $_.Exception.Message
                    } | ConvertTo-Json

                    Invoke-RestMethod `
                        -Method Post `
                        -Uri '{{EscapePowerShell(failUri.AbsoluteUri)}}' `
                        -ContentType 'application/json' `
                        -Body $body | Out-Null
                }
                catch {
                }
            }
            finally {
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            }
            """;
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

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''");
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodeRegex();
}
