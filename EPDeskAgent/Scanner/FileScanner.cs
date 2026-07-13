using EPDeskAgent.Database;
using EPDeskAgent.Models;

namespace EPDeskAgent.Scanner;

public class FileScanner
{
    private readonly IConfiguration _configuration;
    private readonly FileMetadataRepository _repository;
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(
        IConfiguration configuration,
        FileMetadataRepository repository,
        ILogger<FileScanner> logger)
    {
        _configuration = configuration;
        _repository = repository;
        _logger = logger;
    }

    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        return ScanAsync(null, cancellationToken);
    }

    public async Task ScanAsync(
        ScanExclusionsResponse? serverExclusions,
        CancellationToken cancellationToken = default)
    {
        var deviceCode = _configuration["Agent:DeviceCode"];

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            deviceCode = Environment.MachineName;
        }

        deviceCode = deviceCode.Trim().ToUpperInvariant();

        var scanFolders = GetScanFolders();

        var configuredExcludedFolders = _configuration
            .GetSection("Agent:ExcludedFolders")
            .Get<string[]>() ?? [];

        var configuredExcludedFolderNames = _configuration
            .GetSection("Agent:ExcludedFolderNames")
            .Get<string[]>() ?? [];

        var configuredExcludedFileExtensions = _configuration
            .GetSection("Agent:ExcludedFileExtensions")
            .Get<string[]>() ?? [];

        var excludedFolders = MergeExclusions(
            configuredExcludedFolders,
            serverExclusions?.ExcludedFolders
        );

        var excludedFolderNames = MergeExclusions(
            configuredExcludedFolderNames,
            serverExclusions?.ExcludedFolderNames
        );

        var excludedFileExtensions = MergeExclusions(
            configuredExcludedFileExtensions,
            serverExclusions?.ExcludedFileExtensions,
            normalizeFileExtensions: true
        );

        var scanBatchSize = _configuration.GetValue<int>("Agent:FileScanBatchSize");
        if (scanBatchSize <= 0)
        {
            scanBatchSize = 500;
        }

        var pauseEveryFiles = _configuration.GetValue<int>("Agent:FileScanPauseEveryFiles");
        var pauseMilliseconds = _configuration.GetValue<int>("Agent:FileScanPauseMilliseconds");

        var options = new ScanFilterOptions
        {
            SkipHiddenFolders = _configuration.GetValue<bool>("Agent:SkipHiddenFolders"),
            SkipSystemFolders = _configuration.GetValue<bool>("Agent:SkipSystemFolders"),
            SkipDotFolders = _configuration.GetValue<bool>("Agent:SkipDotFolders"),
            SkipHiddenFiles = _configuration.GetValue<bool>("Agent:SkipHiddenFiles"),
            SkipSystemFiles = _configuration.GetValue<bool>("Agent:SkipSystemFiles"),
            SkipDotFiles = _configuration.GetValue<bool>("Agent:SkipDotFiles")
        };

        foreach (var folder in scanFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(folder))
            {
                _logger.LogWarning("Scan folder does not exist: {Folder}", folder);
                continue;
            }

            _logger.LogInformation("Scanning folder: {Folder}", folder);

            var count = await ScanFolderAsync(
                deviceCode,
                folder,
                excludedFolders,
                excludedFolderNames,
                excludedFileExtensions,
                options,
                scanBatchSize,
                pauseEveryFiles,
                pauseMilliseconds,
                cancellationToken
            );

            _logger.LogInformation("Finished scanning {Folder}. Files scanned: {Count}", folder, count);
        }
    }

    private string[] GetScanFolders()
    {
        var scanAllFixedDrives = _configuration.GetValue<bool>("Agent:ScanAllFixedDrives");

        if (scanAllFixedDrives)
        {
            var drives = DriveInfo
                .GetDrives()
                .Where(drive =>
                    drive.IsReady &&
                    drive.DriveType == DriveType.Fixed)
                .Select(drive => drive.RootDirectory.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _logger.LogInformation(
                "Auto detected fixed drives: {Drives}",
                string.Join(", ", drives)
            );

            return drives;
        }

        return _configuration
            .GetSection("Agent:ScanFolders")
            .Get<string[]>() ?? [];
    }

    private static string[] MergeExclusions(
        IEnumerable<string> configuredValues,
        IEnumerable<string>? serverValues,
        bool normalizeFileExtensions = false)
    {
        var values = new List<string>();

        values.AddRange(configuredValues);

        if (serverValues != null)
        {
            values.AddRange(serverValues);
        }

        return values
            .Select(value => NormalizeExclusionValue(value, normalizeFileExtensions))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeExclusionValue(
        string value,
        bool normalizeFileExtension)
    {
        var normalizedValue = value.Trim();

        if (normalizedValue == "")
        {
            return "";
        }

        if (!normalizeFileExtension)
        {
            return normalizedValue.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
        }

        normalizedValue = normalizedValue.TrimStart('.');

        return normalizedValue == ""
            ? ""
            : "." + normalizedValue.ToLowerInvariant();
    }

    private async Task<int> ScanFolderAsync(
        string deviceCode,
        string rootFolder,
        string[] excludedFolders,
        string[] excludedFolderNames,
        string[] excludedFileExtensions,
        ScanFilterOptions options,
        int scanBatchSize,
        int pauseEveryFiles,
        int pauseMilliseconds,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var foldersToScan = new Stack<string>();
        var pendingFiles = new List<FileMetadata>(scanBatchSize);

        foldersToScan.Push(rootFolder);

        while (foldersToScan.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentFolder = foldersToScan.Pop();

            if (IsDirectoryExcluded(
                    currentFolder,
                    excludedFolders,
                    excludedFolderNames,
                    options))
            {
                continue;
            }

            string[] subFolders;

            try
            {
                subFolders = Directory.GetDirectories(currentFolder);
            }
            catch
            {
                continue;
            }

            foreach (var subFolder in subFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsDirectoryExcluded(
                        subFolder,
                        excludedFolders,
                        excludedFolderNames,
                        options))
                {
                    foldersToScan.Push(subFolder);
                }
            }

            string[] files;

            try
            {
                files = Directory.GetFiles(currentFolder);
            }
            catch
            {
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsFileExcluded(
                        filePath,
                        excludedFileExtensions,
                        options))
                {
                    continue;
                }

                FileInfo info;

                try
                {
                    info = new FileInfo(filePath);
                }
                catch
                {
                    continue;
                }

                var metadata = new FileMetadata
                {
                    DeviceCode = deviceCode,
                    FullPath = info.FullName,
                    DirectoryPath = info.DirectoryName ?? "",
                    FileName = info.Name,
                    Extension = info.Extension,
                    SizeBytes = info.Length,
                    CreatedAtUtc = info.CreationTimeUtc,
                    UpdatedAtUtc = info.LastWriteTimeUtc,
                    LastSeenAtUtc = DateTime.UtcNow,
                    IsDeleted = false,
                    SyncStatus = "pending"
                };

                pendingFiles.Add(metadata);

                count++;

                if (pendingFiles.Count >= scanBatchSize)
                {
                    await _repository.UpsertFilesAsync(pendingFiles, cancellationToken);
                    pendingFiles.Clear();
                }

                if (count % 1000 == 0)
                {
                    _logger.LogInformation("Scanned {Count} files...", count);
                }

                if (pauseEveryFiles > 0 &&
                    pauseMilliseconds > 0 &&
                    count % pauseEveryFiles == 0)
                {
                    await Task.Delay(pauseMilliseconds, cancellationToken);
                }
            }
        }

        if (pendingFiles.Count > 0)
        {
            await _repository.UpsertFilesAsync(pendingFiles, cancellationToken);
        }

        return count;
    }

    private static bool IsDirectoryExcluded(
        string folderPath,
        string[] excludedFolders,
        string[] excludedFolderNames,
        ScanFilterOptions options)
    {
        var folderName = Path.GetFileName(
            folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        );

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        foreach (var excludedFolder in excludedFolders)
        {
            if (string.IsNullOrWhiteSpace(excludedFolder))
            {
                continue;
            }

            if (IsFolderPathExcluded(folderPath, folderName, excludedFolder))
            {
                return true;
            }
        }

        foreach (var excludedName in excludedFolderNames)
        {
            if (folderName.Equals(
                    excludedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (options.SkipDotFolders && folderName.StartsWith("."))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(folderPath);

            if (options.SkipHiddenFolders &&
                attributes.HasFlag(FileAttributes.Hidden))
            {
                return true;
            }

            if (options.SkipSystemFolders &&
                attributes.HasFlag(FileAttributes.System))
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsFolderPathExcluded(
        string folderPath,
        string folderName,
        string excludedFolder)
    {
        excludedFolder = excludedFolder.Trim();

        if (excludedFolder == "")
        {
            return false;
        }

        if (IsSimpleFolderName(excludedFolder))
        {
            return folderName.Equals(
                excludedFolder,
                StringComparison.OrdinalIgnoreCase
            );
        }

        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        var normalizedExcludedFolder = NormalizeFolderPath(excludedFolder);

        if (normalizedExcludedFolder == "")
        {
            return false;
        }

        if (normalizedFolderPath.Equals(
                normalizedExcludedFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedFolderPath.StartsWith(
                   normalizedExcludedFolder + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedFolderPath.StartsWith(
                   normalizedExcludedFolder + Path.AltDirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimpleFolderName(string value)
    {
        return !Path.IsPathRooted(value) &&
               !value.Contains(Path.DirectorySeparatorChar) &&
               !value.Contains(Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return folderPath
            .Trim()
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
    }

    private static bool IsFileExcluded(
        string filePath,
        string[] excludedFileExtensions,
        ScanFilterOptions options)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        if (options.SkipDotFiles && fileName.StartsWith("."))
        {
            return true;
        }

        foreach (var excludedExtension in excludedFileExtensions)
        {
            if (extension.Equals(
                    excludedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        try
        {
            var attributes = File.GetAttributes(filePath);

            if (options.SkipHiddenFiles &&
                attributes.HasFlag(FileAttributes.Hidden))
            {
                return true;
            }

            if (options.SkipSystemFiles &&
                attributes.HasFlag(FileAttributes.System))
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }
}

public class ScanFilterOptions
{
    public bool SkipHiddenFolders { get; set; }
    public bool SkipSystemFolders { get; set; }
    public bool SkipDotFolders { get; set; }

    public bool SkipHiddenFiles { get; set; }
    public bool SkipSystemFiles { get; set; }
    public bool SkipDotFiles { get; set; }
}
