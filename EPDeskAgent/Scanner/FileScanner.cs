using EPDeskAgent.Database;
using EPDeskAgent.Models;

namespace EPDeskAgent.Scanner
{
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
            var deviceCode = _configuration["Agent:DeviceCode"] ?? Environment.MachineName;

            var scanFolders = _configuration
                .GetSection("Agent:ScanFolders")
                .Get<string[]>() ?? [];

            var excludedFolders = _configuration
                .GetSection("Agent:ExcludedFolders")
                .Get<string[]>() ?? [];

            foreach (var folder in scanFolders)
            {
                if (!Directory.Exists(folder))
                {
                    _logger.LogWarning("Scan folder does not exist: {Folder}", folder);
                    continue;
                }

                _logger.LogInformation("Scanning folder: {Folder}", folder);

                await ScanFolderAsync(deviceCode, folder, excludedFolders);
            }
        }

        private async Task ScanFolderAsync(
            string deviceCode,
            string rootFolder,
            string[] excludedFolders)
        {
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(
                    rootFolder,
                    "*.*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not scan folder: {Folder}", rootFolder);
                return;
            }

            var count = 0;

            foreach (var filePath in files)
            {
                if (IsExcluded(filePath, excludedFolders))
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

            _logger.LogInformation("Finished scanning {Folder}. Files scanned: {Count}", rootFolder, count);
        }

        private static bool IsExcluded(string filePath, string[] excludedFolders)
        {
            foreach (var excluded in excludedFolders)
            {
                if (filePath.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}