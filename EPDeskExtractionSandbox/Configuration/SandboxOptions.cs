using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Configuration;

public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    public string SharedApiKey { get; set; } = "";
    public string ApiKeyHeader { get; set; } = "X-Extraction-Key";
    public string FileNameHeader { get; set; } = "X-File-Name";
    public string TempRoot { get; set; } = "/tmp/epdesk-extraction-sandbox";
    public int MaxConcurrentRequests { get; set; } = 1;
    public int RequestTimeoutSeconds { get; set; } = 300;
    public long MaxInputBytes { get; set; } = 25L * 1024 * 1024;
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxTextCharacters { get; set; } = 4 * 1024 * 1024;
    public int MaxXmlCharacters { get; set; } = 16 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 2_000;
    public long MaxArchiveEntryBytes { get; set; } = 32L * 1024 * 1024;
    public long MaxArchiveExpandedBytes { get; set; } = 64L * 1024 * 1024;
    public double MaxCompressionRatio { get; set; } = 50;
    public int MaxTableRows { get; set; } = 20_000;
    public int MaxTableColumns { get; set; } = 512;
    public int MaxCellCharacters { get; set; } = 100_000;
    public int SampleRowCount { get; set; } = 10;
    public int MaxExternalOutputCharacters { get; set; } = 8 * 1024 * 1024;
    public int ExternalProcessTimeoutSeconds { get; set; } = 180;
    public bool AllowSpeakerNotes { get; set; }
    public bool AllowWordComments { get; set; }
    public bool AllowImageOcr { get; set; }
    public string DefaultOcrLanguage { get; set; } = "eng";
    public string[] AllowedOcrLanguages { get; set; } = ["eng"];
    public string[] AllowedExtensions { get; set; } =
    [
        ".pdf", ".docx", ".pptx", ".xlsx", ".csv", ".txt", ".md",
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    ];
    public string ClamSocketPath { get; set; } = "/run/clamav/clamd.sock";
    public string ClamDatabasePath { get; set; } = "/var/lib/clamav";
    public string MinimumClamVersion { get; set; } = "1.4.5";
    public int MaxClamSignatureAgeHours { get; set; } = 72;
    public int ClamDaemonConnectTimeoutSeconds { get; set; } = 5;
    public int MalwareScanTimeoutSeconds { get; set; } = 120;
}

public sealed class SandboxOptionsValidator : IValidateOptions<SandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SandboxOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SharedApiKey) || options.SharedApiKey.Length < 32)
        {
            failures.Add("Sandbox:SharedApiKey must contain at least 32 characters.");
        }

        if (!IsSafeHeaderName(options.ApiKeyHeader) || !IsSafeHeaderName(options.FileNameHeader))
        {
            failures.Add("Sandbox header names must contain only letters, digits, and hyphens.");
        }

        if (options.MaxConcurrentRequests is < 1 or > 8)
        {
            failures.Add("Sandbox:MaxConcurrentRequests must be between 1 and 8.");
        }

        if (options.RequestTimeoutSeconds <= 0 || options.ExternalProcessTimeoutSeconds <= 0 ||
            options.MalwareScanTimeoutSeconds is < 1 or > 900 ||
            options.ClamDaemonConnectTimeoutSeconds is < 1 or > 30 ||
            options.MalwareScanTimeoutSeconds > options.RequestTimeoutSeconds)
        {
            failures.Add("Sandbox timeout values are invalid or the malware timeout exceeds the request timeout.");
        }

        if (options.MaxInputBytes is <= 0 or > 536_870_912 || options.MaxResponseBytes <= 0 ||
            options.MaxTextCharacters <= 0 || options.MaxXmlCharacters <= 0 ||
            options.MaxArchiveEntries is <= 0 or > 100_000 || options.MaxArchiveEntryBytes <= 0 ||
            options.MaxArchiveExpandedBytes <= 0 || options.MaxCompressionRatio <= 0 ||
            options.MaxTableRows <= 0 || options.MaxTableColumns <= 0 ||
            options.MaxCellCharacters <= 0 || options.SampleRowCount < 0 ||
            options.MaxExternalOutputCharacters <= 0)
        {
            failures.Add("Sandbox extraction limits must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.TempRoot) ||
            !options.TempRoot.StartsWith("/tmp/", StringComparison.Ordinal) ||
            options.TempRoot == "/tmp/epdesk-clamd" ||
            options.TempRoot.StartsWith("/tmp/epdesk-clamd/", StringComparison.Ordinal) ||
            IsFilesystemRoot(options.TempRoot))
        {
            failures.Add("Sandbox:TempRoot must be a non-root, non-scanner directory under /tmp.");
        }

        if (!IsSafeContainerPath(options.ClamSocketPath, 100) ||
            !options.ClamSocketPath.StartsWith("/run/clamav/", StringComparison.Ordinal) ||
            options.ClamSocketPath == "/run/clamav/clamd.pid" ||
            !IsSafeContainerPath(options.ClamDatabasePath, 200) ||
            !(options.ClamDatabasePath == "/var/lib/clamav" ||
              options.ClamDatabasePath.StartsWith("/var/lib/clamav/", StringComparison.Ordinal)) ||
            !Version.TryParse(options.MinimumClamVersion, out _) ||
            options.MaxClamSignatureAgeHours is < 1 or > 720)
        {
            failures.Add("ClamAV socket/database paths, minimum version, or signature age are invalid.");
        }

        var allowedExtensions = (options.AllowedExtensions ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (allowedExtensions.Length == 0 || allowedExtensions.Any(value =>
                !value.StartsWith('.') || value.Length > 10 ||
                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.')))
        {
            failures.Add("Sandbox:AllowedExtensions contains an invalid extension.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultOcrLanguage) ||
            options.AllowedOcrLanguages is null || options.AllowedOcrLanguages.Length == 0 ||
            !options.AllowedOcrLanguages.Contains(options.DefaultOcrLanguage, StringComparer.OrdinalIgnoreCase) ||
            options.AllowedOcrLanguages.Any(value => !IsSafeOcrLanguage(value)))
        {
            failures.Add("Sandbox OCR languages are invalid or do not include the default language.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSafeHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSafeOcrLanguage(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '+');

    private static bool IsSafeContainerPath(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !value.StartsWith('/') || value == "/" ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '_' and not '-' and not '.'))
        {
            return false;
        }

        return !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static bool IsFilesystemRoot(string value)
    {
        try
        {
            var fullPath = Path.GetFullPath(value);
            return string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
