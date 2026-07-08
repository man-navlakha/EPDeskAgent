using System.IO.Compression;

namespace EPDeskAgent.Services;

public class ZipService
{
    private readonly ILogger<ZipService> _logger;

    public ZipService(ILogger<ZipService> logger)
    {
        _logger = logger;
    }

    public string CreateZipFromFolder(string folderPath, Guid requestId)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("Folder path is empty.");
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        var zipPath = GetZipPath(requestId);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var files = EnumerateFilesSafe(folderPath).ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("Folder does not contain accessible files.");
        }

        foreach (var file in files)
        {
            try
            {
                var relativePath = Path.GetRelativePath(folderPath, file);
                relativePath = MakeZipEntrySafe(relativePath);

                archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Fastest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add file to ZIP: {File}", file);
            }
        }

        return zipPath;
    }

    public string CreateZipFromFiles(List<string> filePaths, Guid requestId)
    {
        if (filePaths == null || filePaths.Count == 0)
        {
            throw new InvalidOperationException("No files provided for ZIP.");
        }

        var validFiles = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validFiles.Count == 0)
        {
            throw new InvalidOperationException("No valid file paths provided for ZIP.");
        }

        var missingFiles = validFiles
            .Where(path => !File.Exists(path))
            .ToList();

        if (missingFiles.Count > 0)
        {
            throw new FileNotFoundException(
                "Some requested files were not found: " + string.Join(", ", missingFiles)
            );
        }

        var zipPath = GetZipPath(requestId);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var file in validFiles)
        {
            try
            {
                var entryName = MakeEntryNameFromFullPath(file);

                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add file to ZIP: {File}", file);
            }
        }

        return zipPath;
    }

    private string GetZipPath(Guid requestId)
    {
        var tempZipDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EPDeskAgent",
            "TempZips"
        );

        Directory.CreateDirectory(tempZipDir);

        return Path.Combine(tempZipDir, $"{requestId}.zip");
    }

    private IEnumerable<string> EnumerateFilesSafe(string rootFolder)
    {
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> directories = Array.Empty<string>();

            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                // Ignore inaccessible folder
            }

            foreach (var file in files)
            {
                yield return file;
            }

            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                // Ignore inaccessible folder
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private string MakeEntryNameFromFullPath(string fullPath)
    {
        var safe = fullPath
            .Replace(":", "")
            .Replace("\\", "/")
            .TrimStart('/');

        return MakeZipEntrySafe(safe);
    }

    private string MakeZipEntrySafe(string entryName)
    {
        entryName = entryName.Replace("\\", "/");

        while (entryName.Contains("../"))
        {
            entryName = entryName.Replace("../", "");
        }

        return entryName.TrimStart('/');
    }
}