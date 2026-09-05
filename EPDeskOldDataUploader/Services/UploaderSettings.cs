using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Remembers what was typed into the window between runs. The API key is kept
/// under DPAPI for the current user rather than in clear text, because the
/// settings file sits in a roaming-adjacent profile folder.
/// </summary>
public sealed class UploaderSettings
{
    private const string ProtectionPurpose = "EPDeskOldDataUploader.ApiKey.v1";

    public string ApiBaseUrl { get; set; } = "https://laptop-data.excellentpublicity.co";

    public string RootPath { get; set; } = "";

    public string DeviceCodePrefix { get; set; } = "OLD-";

    public int ParallelUploads { get; set; } = 4;

    public bool ComputeSha256 { get; set; } = true;

    /// <summary>
    /// True when the selected folder is one person's archive rather than the
    /// root holding a folder per person.
    /// </summary>
    public bool SinglePersonFolder { get; set; }

    /// <summary>
    /// Store objects as uploads/old-user-data/{person}/{path} rather than the
    /// agent's hashed layout. Requires a server build that has the
    /// old-user-data push endpoints.
    /// </summary>
    public bool UseReadableFolders { get; set; }

    public int RetryCount { get; set; } = 3;

    /// <summary>DPAPI ciphertext. Never the key itself.</summary>
    public string ProtectedApiKey { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; set; } = "";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EPDeskOldDataUploader",
        "settings.json"
    );

    public static UploaderSettings Load()
    {
        var settings = ReadFile() ?? new UploaderSettings();

        settings.ApiKey = Unprotect(settings.ProtectedApiKey);

        if (settings.ApiKey.Length == 0)
        {
            settings.ApiKey = FindApiKeyInEnvironment();
        }

        if (settings.ParallelUploads < 1)
        {
            settings.ParallelUploads = 4;
        }

        if (settings.RetryCount < 1)
        {
            settings.RetryCount = 3;
        }

        return settings;
    }

    public void Save()
    {
        ProtectedApiKey = Protect(ApiKey);

        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
        );
    }

    private static UploaderSettings? ReadFile()
    {
        try
        {
            var path = SettingsPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UploaderSettings>(File.ReadAllText(path))
                : null;
        }
        catch (Exception)
        {
            // A corrupt settings file must not stop the tool from opening.
            return null;
        }
    }

    /// <summary>
    /// Seeds the key from the same places the rest of the repo reads it: the
    /// process/user environment first, then a .env beside the executable or in a
    /// parent folder, so running from the repo needs no typing at all.
    /// </summary>
    private static string FindApiKeyInEnvironment()
    {
        foreach (var name in new[]
                 {
                     "Security__AgentFileUploadApiKey",
                     "EPDESK_AGENT_FILE_UPLOAD_KEY"
                 })
        {
            foreach (var target in new[]
                     {
                         EnvironmentVariableTarget.Process,
                         EnvironmentVariableTarget.User,
                         EnvironmentVariableTarget.Machine
                     })
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(name, target);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
                catch (Exception)
                {
                    // Reading the machine scope can be denied; keep looking.
                }
            }
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < 8 && directory != null; depth++)
        {
            var envFile = Path.Combine(directory.FullName, ".env");

            if (File.Exists(envFile))
            {
                var value = ReadEnvFileValue(envFile, "Security__AgentFileUploadApiKey");
                if (value.Length > 0)
                {
                    return value;
                }
            }

            directory = directory.Parent;
        }

        return "";
    }

    private static string ReadEnvFileValue(string envFile, string key)
    {
        try
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                var trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');

                if (separator > 0 &&
                    trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(separator + 1)..].Trim().Trim('"');
                }
            }
        }
        catch (Exception)
        {
            // An unreadable .env is not worth failing startup over.
        }

        return "";
    }

    private static string Protect(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Encoding.UTF8.GetBytes(ProtectionPurpose),
                DataProtectionScope.CurrentUser
            ));
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(value),
                Encoding.UTF8.GetBytes(ProtectionPurpose),
                DataProtectionScope.CurrentUser
            ));
        }
        catch (Exception)
        {
            // Written by a different user or machine - just ask for it again.
            return "";
        }
    }
}
