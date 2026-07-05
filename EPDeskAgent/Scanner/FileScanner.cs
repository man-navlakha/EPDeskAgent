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

    public async Task ScanAsync()
    {
        var deviceCode = _configuration["Agent:DeviceCode"];

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            deviceCode = Environment.MachineName;
        }

        var scanFolders = GetScanFolders();

        var excludedFolders = _configuration
            .GetSection("Agent:ExcludedFolders")
            .Get<string[]>() ?? [];

        var excludedFolderNames = _configuration
            .GetSection("Agent:ExcludedFolderNames")
            .Get<string[]>() ?? [];

        var excludedFileExtensions = _configuration
            .GetSection("Agent:ExcludedFileExtensions")
            .Get<string[]>() ?? [];

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
                options
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

    private async Task<int> ScanFolderAsync(
        string deviceCode,
        string rootFolder,
        string[] excludedFolders,
        string[] excludedFolderNames,
        string[] excludedFileExtensions,
        ScanFilterOptions options)
    {
        var count = 0;
        var foldersToScan = new Stack<string>();

        foldersToScan.Push(rootFolder);

        while (foldersToScan.Count > 0)
        {
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

                await _repository.UpsertFileAsync(metadata);

                count++;

                if (count % 1000 == 0)
                {
                    _logger.LogInformation("Scanned {Count} files...", count);
                }
            }
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

            if (folderPath.StartsWith(
                    excludedFolder,
                    StringComparison.OrdinalIgnoreCase))
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