using EPDeskAgent.Database;
using EPDeskAgent.Models;
using System.Security.Cryptography;

namespace EPDeskAgent.Services;

public sealed class AutomaticFileUploadService
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly IConfiguration _configuration;
    private readonly FileMetadataRepository _repository;
    private readonly ApiClientService _apiClient;
    private readonly LocalLogService _localLogService;
    private readonly ILogger<AutomaticFileUploadService> _logger;

    public AutomaticFileUploadService(
        IConfiguration configuration,
        FileMetadataRepository repository,
        ApiClientService apiClient,
        LocalLogService localLogService,
        ILogger<AutomaticFileUploadService> logger)
    {
        _configuration = configuration;
        _repository = repository;
        _apiClient = apiClient;
        _localLogService = localLogService;
        _logger = logger;
    }

    public async Task UploadChangedFilesAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await UploadChangedFilesCoreAsync(cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task UploadChangedFilesCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await _apiClient.GetAutomaticFileUploadPolicyAsync(cancellationToken);

        if (!policy.IsEnabled || policy.Extensions.Count == 0)
        {
            _logger.LogInformation("Automatic Backblaze file upload is disabled or has no extensions.");
            return;
        }

        var extensions = policy.Extensions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var batchSize = _configuration.GetValue<int>("Agent:AutomaticUploadBatchSize");
        if (batchSize <= 0)
        {
            batchSize = 25;
        }

        long afterId = 0;
        var completedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;
        var consecutiveFailures = 0;
        var stopUploadPass = false;

        var failureThreshold = _configuration.GetValue<int>(
            "Agent:AutomaticUploadFailureThreshold"
        );

        if (failureThreshold <= 0)
        {
            failureThreshold = 3;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var files = await _repository.GetFilesPendingAutomaticUploadAsync(
                extensions,
                afterId,
                batchSize
            );

            if (files.Count == 0)
            {
                break;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                afterId = file.Id;

                var result = await UploadOneFileAsync(
                    file,
                    policy.MaxFileSizeBytes,
                    cancellationToken
                );

                if (result == AutomaticUploadResult.Uploaded)
                {
                    completedCount++;
                    consecutiveFailures = 0;
                }
                else if (result == AutomaticUploadResult.FileFailed)
                {
                    // One bad file must not stop the pass. It has already been
                    // counted against its own retry budget, so move on.
                    failedCount++;
                }
                else if (result == AutomaticUploadResult.SystemFailed)
                {
                    failedCount++;
                    consecutiveFailures++;

                    if (consecutiveFailures >= failureThreshold)
                    {
                        stopUploadPass = true;

                        _localLogService.Warning(
                            "upload",
                            $"Automatic upload pass stopped after {consecutiveFailures} consecutive storage failures. It will retry during the next scheduled pass.",
                            step: "automatic_upload_circuit_open"
                        );

                        break;
                    }
                }
                else
                {
                    skippedCount++;
                }
            }

            if (stopUploadPass)
            {
                break;
            }
        }

        _localLogService.Info(
            "upload",
            $"Automatic Backblaze upload pass completed. Uploaded: {completedCount}. Skipped: {skippedCount}. Failed: {failedCount}.",
            step: "automatic_upload_pass_completed"
        );
    }

    private async Task<AutomaticUploadResult> UploadOneFileAsync(
        FileMetadata file,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        Guid? serverUploadId = null;

        try
        {
            if (!File.Exists(file.FullPath))
            {
                throw new FileNotFoundException($"File no longer exists: {file.FullPath}");
            }

            if (file.SizeBytes <= 0)
            {
                throw new InvalidOperationException("Empty files cannot be uploaded.");
            }

            if (file.SizeBytes > maxFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"File size {file.SizeBytes} exceeds policy limit {maxFileSizeBytes}."
                );
            }

            var currentInfo = new FileInfo(file.FullPath);

            if (currentInfo.Length != file.SizeBytes ||
                currentInfo.LastWriteTimeUtc != file.UpdatedAtUtc)
            {
                await _repository.UpsertFileAsync(
                    new FileMetadata
                    {
                        DeviceCode = file.DeviceCode,
                        FullPath = currentInfo.FullName,
                        DirectoryPath = currentInfo.DirectoryName ?? "",
                        FileName = currentInfo.Name,
                        Extension = currentInfo.Extension,
                        SizeBytes = currentInfo.Length,
                        CreatedAtUtc = currentInfo.CreationTimeUtc,
                        UpdatedAtUtc = currentInfo.LastWriteTimeUtc,
                        LastSeenAtUtc = DateTime.UtcNow
                    },
                    cancellationToken
                );

                _logger.LogInformation(
                    "Skipped upload because file changed after scanning: {FilePath}",
                    file.FullPath
                );

                return AutomaticUploadResult.Skipped;
            }

            await _repository.MarkAutomaticUploadStartedAsync(file.Id);

            var sha256 = await CalculateSha256Async(
                file.FullPath,
                cancellationToken
            );
            currentInfo.Refresh();
            if (!currentInfo.Exists ||
                currentInfo.Length != file.SizeBytes ||
                currentInfo.LastWriteTimeUtc != file.UpdatedAtUtc)
            {
                throw new IOException("File changed while its checksum was being calculated.");
            }

            var upload = await _apiClient.InitiateAutomaticFileUploadAsync(
                file,
                sha256,
                cancellationToken
            );

            serverUploadId = upload.UploadId;

            if (!upload.ShouldUpload)
            {
                await _repository.MarkAutomaticUploadCompletedAsync(file);
                return AutomaticUploadResult.Uploaded;
            }

            var uploadedPartNumbers = upload.UploadedPartNumbers.ToHashSet();

            for (var partNumber = 1;
                 partNumber <= upload.ExpectedPartCount;
                 partNumber++)
            {
                if (uploadedPartNumbers.Contains(partNumber))
                {
                    continue;
                }

                await UploadPartWithRetryAsync(
                    file.FullPath,
                    upload.UploadId,
                    partNumber,
                    cancellationToken
                );
            }

            // Detect a file that changed while its parts were being uploaded.
            currentInfo.Refresh();
            if (!currentInfo.Exists ||
                currentInfo.Length != file.SizeBytes ||
                currentInfo.LastWriteTimeUtc != file.UpdatedAtUtc)
            {
                throw new IOException("File changed while it was being uploaded.");
            }

            var postUploadSha256 = await CalculateSha256Async(
                file.FullPath,
                cancellationToken
            );
            if (!string.Equals(
                    sha256,
                    postUploadSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("File content changed while it was being uploaded.");
            }

            await _apiClient.CompleteAutomaticFileUploadAsync(
                upload.UploadId,
                cancellationToken
            );

            await _repository.MarkAutomaticUploadCompletedAsync(file);

            _localLogService.Info(
                "upload",
                $"Automatic Backblaze upload completed: {file.FullPath}",
                step: "automatic_upload_completed"
            );

            return AutomaticUploadResult.Uploaded;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            await _repository.MarkFileMissingAsync(file.Id);

            _logger.LogInformation(
                "Skipping automatic upload because the local file is missing: {FilePath}",
                file.FullPath
            );

            _localLogService.Warning(
                "upload",
                $"Local file is missing and was removed from the automatic upload queue: {file.FullPath}",
                step: "automatic_upload_file_missing"
            );

            return AutomaticUploadResult.Skipped;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Automatic Backblaze upload failed: {FilePath}",
                file.FullPath
            );

            await _repository.MarkAutomaticUploadFailedAsync(file.Id, exception.Message);

            if (serverUploadId.HasValue)
            {
                await _apiClient.ReportAutomaticFileUploadFailureAsync(
                    serverUploadId.Value,
                    exception.Message,
                    cancellationToken
                );
            }

            _localLogService.Error(
                "upload",
                $"Automatic Backblaze upload failed: {file.FullPath}",
                exception,
                step: "automatic_upload_failed"
            );

            return IsSingleFileFailure(exception)
                ? AutomaticUploadResult.FileFailed
                : AutomaticUploadResult.SystemFailed;
        }
    }

    private async Task UploadPartWithRetryAsync(
        string filePath,
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        var retryCount = _configuration.GetValue<int>("Agent:AutomaticUploadRetryCount");
        if (retryCount <= 0)
        {
            retryCount = 3;
        }

        Exception? lastException = null;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                var part = await _apiClient.GetAutomaticUploadPartUrlAsync(
                    uploadId,
                    partNumber,
                    cancellationToken
                );

                await _apiClient.UploadAutomaticFilePartAsync(
                    filePath,
                    part,
                    cancellationToken
                );

                return;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && attempt < retryCount)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException(
            $"Part {partNumber} upload failed after {retryCount} attempts."
        );
    }

    private static string NormalizeExtension(string value)
    {
        value = value.Trim().TrimStart('.');
        return value.Length == 0 ? "" : "." + value.ToLowerInvariant();
    }

    private static async Task<string> CalculateSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private enum AutomaticUploadResult
    {
        Uploaded,
        Skipped,
        /// <summary>
        /// This one file could not be read or accepted. Other files are unaffected,
        /// so the pass keeps going and the file backs off on its own.
        /// </summary>
        FileFailed,
        /// <summary>
        /// The failure looks like it affects every upload (storage, network, auth).
        /// Enough of these in a row and the pass stops early.
        /// </summary>
        SystemFailed
    }

    /// <summary>
    /// Decides whether a failure is specific to one file or points at the storage
    /// backend. Only backend failures are allowed to stop the whole upload pass -
    /// a single unreadable file used to halt every remaining upload on the device.
    /// </summary>
    private static bool IsSingleFileFailure(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => true,
            InvalidOperationException => true,
            // Covers OneDrive placeholders ("Access to the cloud file is denied"),
            // locked files, and files that changed mid-upload.
            IOException => true,
            _ => false
        };
    }
}
