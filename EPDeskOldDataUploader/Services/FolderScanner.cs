using EPDeskOldDataUploader.Models;

namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Walks the "Users Data" root, where every immediate subfolder is one person.
/// Everything below that subfolder belongs to them however deeply it is nested.
///
/// In single-person mode the selected folder is itself the person, so an archive
/// can be uploaded one person at a time. That produces exactly the same device
/// codes and stored paths as scanning the whole root, which is what makes it
/// safe to split a large archive into separate runs.
/// </summary>
public sealed class FolderScanner
{
    private readonly string _rootPath;
    private readonly string _deviceCodePrefix;
    private readonly bool _singlePerson;
    private readonly HashSet<string> _allowedExtensions;
    private readonly long _maxFileSizeBytes;

    public FolderScanner(
        string rootPath,
        string deviceCodePrefix,
        bool singlePerson,
        IEnumerable<string> allowedExtensions,
        long maxFileSizeBytes)
    {
        _singlePerson = singlePerson;
        _rootPath = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _deviceCodePrefix = deviceCodePrefix.Trim();
        _allowedExtensions = allowedExtensions
            .Select(NormalizeExtension)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _maxFileSizeBytes = maxFileSizeBytes;
    }

    public ScanResult Scan(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {_rootPath}");
        }

        var result = new ScanResult();
        var usersByFolder = new Dictionary<string, ScannedUser>(StringComparer.OrdinalIgnoreCase);
        var scannedCount = 0L;

        foreach (var fileInfo in EnumerateFiles(_rootPath, result, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = NormalizeExtension(fileInfo.Extension);

            if (!_allowedExtensions.Contains(extension))
            {
                result.IgnoredByExtensionCount++;
                continue;
            }

            var relativeFromRoot = Path.GetRelativePath(_rootPath, fileInfo.FullName)
                .Replace('\\', '/');

            if (relativeFromRoot == ".." || relativeFromRoot.StartsWith("../"))
            {
                continue;
            }

            string userFolder;
            string relativePath;

            if (_singlePerson)
            {
                // The selected folder is the person, so nothing below it is split off.
                userFolder = Path.GetFileName(_rootPath);
                relativePath = relativeFromRoot;
            }
            else
            {
                var slashIndex = relativeFromRoot.IndexOf('/');

                // A file sitting loose in the root has no owning person folder.
                userFolder = slashIndex < 0 ? "_ROOT" : relativeFromRoot[..slashIndex];
                relativePath = slashIndex < 0
                    ? relativeFromRoot
                    : relativeFromRoot[(slashIndex + 1)..];
            }

            if (!usersByFolder.TryGetValue(userFolder, out var user))
            {
                user = new ScannedUser
                {
                    UserFolder = userFolder,
                    DeviceCode = CreateDeviceCode(_deviceCodePrefix, userFolder)
                };

                usersByFolder[userFolder] = user;
                result.Users.Add(user);
            }

            var file = new ScannedFile
            {
                UserFolder = userFolder,
                DeviceCode = user.DeviceCode,
                FullPath = fileInfo.FullName,
                RelativePath = relativePath,
                FileName = fileInfo.Name,
                Extension = extension,
                SizeBytes = fileInfo.Length,
                LastModifiedAtUtc = fileInfo.LastWriteTimeUtc,
                CreatedAtUtc = fileInfo.CreationTimeUtc
            };

            if (fileInfo.Length <= 0)
            {
                file.State = FileState.Skipped;
                file.Message = "Empty file.";
                user.SkippedCount++;
            }
            else if (_maxFileSizeBytes > 0 && fileInfo.Length > _maxFileSizeBytes)
            {
                file.State = FileState.Skipped;
                file.Message =
                    $"Larger than the server limit of {FormatSize(_maxFileSizeBytes)}.";
                user.SkippedCount++;
            }

            user.Files.Add(file);
            user.TotalBytes += fileInfo.Length;
            scannedCount++;

            if (scannedCount % 500 == 0)
            {
                progress?.Report($"Scanned {scannedCount:N0} matching files...");
            }
        }

        result.Users.Sort((left, right) =>
            string.Compare(left.UserFolder, right.UserFolder, StringComparison.OrdinalIgnoreCase));

        return result;
    }

    /// <summary>
    /// An archive drive nearly always contains a folder this account cannot read.
    /// Those are counted and stepped over rather than aborting the whole scan.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateFiles(
        string rootPath,
        ScanResult result,
        CancellationToken cancellationToken)
    {
        var folders = new Stack<string>();
        folders.Push(rootPath);

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Pop();

            string[] childFolders;

            try
            {
                childFolders = Directory.GetDirectories(folder);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                result.UnreadableFolderCount++;
                continue;
            }

            foreach (var childFolder in childFolders)
            {
                try
                {
                    // Following a junction or symlink can walk in a circle.
                    var attributes = File.GetAttributes(childFolder);

                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        folders.Push(childFolder);
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                    result.UnreadableFolderCount++;
                }
            }

            FileInfo[] files;

            try
            {
                files = new DirectoryInfo(folder).GetFiles();
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                result.UnreadableFolderCount++;
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Matches the device-code shape the server already stores: upper case, and
    /// only characters that survive an object-key path segment.
    /// </summary>
    public static string CreateDeviceCode(string prefix, string userFolder)
    {
        prefix = prefix.Trim();

        // Without this a prefix of "Niraj" and a folder "Desktop" would run
        // together into NIRAJDESKTOP.
        if (prefix.Length > 0 && prefix[^1] is not ('-' or '_' or '.'))
        {
            prefix += "-";
        }

        var value = new string((prefix + userFolder).Trim().ToUpperInvariant()
            .Select(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.' ? x : '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(value) ? "OLD_USER" : value;
    }

    public static string NormalizeExtension(string extension)
    {
        var value = extension.Trim().TrimStart('.');

        return value.Length == 0 ? "" : "." + value.ToLowerInvariant();
    }

    public static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1L << 40 => $"{bytes / (double)(1L << 40):N2} TB",
            >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
            _ => $"{bytes} B"
        };
    }
}
