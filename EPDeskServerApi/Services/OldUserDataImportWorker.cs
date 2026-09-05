using Amazon.S3;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EPDeskServerApi.Services;

public sealed class OldUserDataImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IObjectStorageService _storage;
    private readonly OldUserDataImportOptions _options;
    private readonly B2StorageOptions _storageOptions;
    private readonly ILogger<OldUserDataImportWorker> _logger;

    public OldUserDataImportWorker(
        IServiceScopeFactory scopeFactory,
        IObjectStorageService storage,
        IOptions<OldUserDataImportOptions> options,
        IOptions<B2StorageOptions> storageOptions,
        ILogger<OldUserDataImportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _options = options.Value;
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Old User Data import worker is disabled.");
            return;
        }

        var pollSeconds = Math.Max(1, _options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await FindNextJobAsync(stoppingToken);

                if (jobId.HasValue)
                {
                    await ProcessJobAsync(jobId.Value, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Old User Data import worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }

    private async Task<Guid?> FindNextJobAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OldUserDataImportJobs
            .AsNoTracking()
            .Where(x => x.Status == "pending" ||
                        x.Status == "scanning" ||
                        x.Status == "uploading")
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await GetJobAsync(jobId, cancellationToken);

        if (job == null)
        {
            return;
        }

        try
        {
            if (job.Status is "pending" or "scanning")
            {
                var scanCompleted = await ScanAsync(job, cancellationToken);
                if (!scanCompleted)
                {
                    return;
                }
            }

            try
            {
                await _storage.CheckConnectionAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Old User Data import {JobId} paused because object storage is unavailable.",
                    jobId
                );

                await PauseJobForStorageFailureAsync(
                    jobId,
                    exception.Message,
                    cancellationToken
                );
                return;
            }

            await UploadIndexedFilesAsync(jobId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Old User Data import {JobId} failed.", jobId);
            await MarkJobFailedAsync(jobId, exception.Message, cancellationToken);
        }
    }

    private async Task<bool> ScanAsync(
        OldUserDataImportJob job,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(job.RootPath))
        {
            throw new DirectoryNotFoundException(
                $"Old User Data root is not accessible: {job.RootPath}"
            );
        }

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OldUserDataFiles
                .Where(x => x.ImportJobId == job.Id)
                .ExecuteDeleteAsync(cancellationToken);

            await db.OldUserDataImportJobs
                .Where(x => x.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "scanning")
                    .SetProperty(x => x.IndexedFileCount, 0)
                    .SetProperty(x => x.IndexedSizeBytes, 0)
                    .SetProperty(x => x.UploadedFileCount, 0)
                    .SetProperty(x => x.UploadedSizeBytes, 0)
                    .SetProperty(x => x.FailedFileCount, 0)
                    .SetProperty(x => x.ErrorMessage, "")
                    .SetProperty(x => x.ScanCompletedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.CompletedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken);
        }

        HashSet<string> allowedExtensions;
        long maxFileSizeBytes;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var policyService = scope.ServiceProvider
                .GetRequiredService<FileUploadPolicyService>();
            var policy = await policyService.GetOrCreateAsync(cancellationToken);

            if (!policy.IsEnabled)
            {
                throw new InvalidOperationException("The file upload policy is disabled.");
            }

            allowedExtensions = FileUploadPolicyService.ReadExtensions(policy)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            maxFileSizeBytes = policy.MaxFileSizeBytes;
        }

        if (allowedExtensions.Count == 0)
        {
            throw new InvalidOperationException("The file upload policy has no extensions.");
        }

        var batchSize = Math.Max(1, _options.ScanBatchSize);
        var batch = new List<OldUserDataFile>(batchSize);
        long indexedCount = 0;
        long indexedBytes = 0;

        foreach (var fileInfo in EnumerateFiles(job.RootPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = NormalizeExtension(fileInfo.Extension);
            if (!allowedExtensions.Contains(extension))
            {
                continue;
            }

            var relativeFromRoot = Path.GetRelativePath(job.RootPath, fileInfo.FullName)
                .Replace('\\', '/');

            if (relativeFromRoot == ".." || relativeFromRoot.StartsWith("../"))
            {
                continue;
            }

            var slashIndex = relativeFromRoot.IndexOf('/');
            var userFolder = slashIndex < 0
                ? "_ROOT"
                : relativeFromRoot[..slashIndex];
            var relativePath = slashIndex < 0
                ? relativeFromRoot
                : relativeFromRoot[(slashIndex + 1)..];

            var status = "indexed";
            var errorMessage = "";

            if (fileInfo.Length <= 0)
            {
                status = "skipped";
                errorMessage = "Empty files are not uploaded.";
            }
            else if (fileInfo.Length > maxFileSizeBytes)
            {
                status = "skipped";
                errorMessage = $"File exceeds the upload policy limit of {maxFileSizeBytes} bytes.";
            }
            else if (fileInfo.Length > _storageOptions.PartSizeBytes * 10_000L)
            {
                status = "skipped";
                errorMessage = "File requires more than 10,000 multipart upload parts.";
            }

            batch.Add(new OldUserDataFile
            {
                Id = Guid.NewGuid(),
                ImportJobId = job.Id,
                UserFolder = userFolder,
                DeviceCode = CreateDeviceCode(userFolder),
                FullPath = fileInfo.FullName,
                RelativePath = relativePath,
                FileName = fileInfo.Name,
                Extension = extension,
                SizeBytes = fileInfo.Length,
                CreatedAtUtc = fileInfo.CreationTimeUtc,
                UpdatedAtUtc = fileInfo.LastWriteTimeUtc,
                ContentType = GetContentType(extension),
                ObjectKey = CreateObjectKey(userFolder, relativePath),
                PartSizeBytes = _storageOptions.PartSizeBytes,
                Status = status,
                ErrorMessage = errorMessage,
                IndexedAtUtc = DateTime.UtcNow
            });

            indexedCount++;
            indexedBytes += fileInfo.Length;

            if (batch.Count >= batchSize)
            {
                await SaveScanBatchAsync(
                    job.Id,
                    batch,
                    indexedCount,
                    indexedBytes,
                    cancellationToken
                );
                batch.Clear();

                if (await IsJobPausedAsync(job.Id, cancellationToken))
                {
                    return false;
                }
            }
        }

        if (batch.Count > 0)
        {
            await SaveScanBatchAsync(
                job.Id,
                batch,
                indexedCount,
                indexedBytes,
                cancellationToken
            );
        }

        if (await IsJobPausedAsync(job.Id, cancellationToken))
        {
            return false;
        }

        await using var finalScope = _scopeFactory.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await finalDb.OldUserDataImportJobs
            .Where(x => x.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "uploading")
                .SetProperty(x => x.IndexedFileCount, indexedCount)
                .SetProperty(x => x.IndexedSizeBytes, indexedBytes)
                .SetProperty(x => x.ScanCompletedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);

        _logger.LogInformation(
            "Old User Data scan completed. Job: {JobId}, Files: {FileCount}, Bytes: {Bytes}",
            job.Id,
            indexedCount,
            indexedBytes
        );

        return true;
    }

    private async Task SaveScanBatchAsync(
        Guid jobId,
        IReadOnlyCollection<OldUserDataFile> files,
        long indexedCount,
        long indexedBytes,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OldUserDataFiles.AddRange(files);
        await db.SaveChangesAsync(cancellationToken);

        await db.OldUserDataImportJobs
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IndexedFileCount, indexedCount)
                .SetProperty(x => x.IndexedSizeBytes, indexedBytes)
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    private async Task UploadIndexedFilesAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var concurrency = Math.Clamp(_options.UploadConcurrency, 1, 32);

        _logger.LogInformation(
            "Old User Data upload starting with {Concurrency} concurrent file(s).",
            concurrency
        );

        // Each loop claims its own files, so they never collide. The claim is a
        // database-level lease, which also makes it safe to run a second API
        // instance against the same job.
        var loops = Enumerable
            .Range(0, concurrency)
            .Select(_ => UploadLoopAsync(jobId, cancellationToken))
            .ToArray();

        await Task.WhenAll(loops);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Only finish the job once every loop has drained, otherwise a job would be
        // marked complete while other workers still hold leases.
        if (!await HasClaimableFilesAsync(jobId, cancellationToken))
        {
            await CompleteJobAsync(jobId, cancellationToken);
        }
    }

    private async Task UploadLoopAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var jobStatus = await GetJobStatusAsync(jobId, cancellationToken);

            if (jobStatus == null || jobStatus == "paused")
            {
                return;
            }

            var fileId = await ClaimNextFileAsync(jobId, cancellationToken);

            if (!fileId.HasValue)
            {
                return;
            }

            try
            {
                await UploadOneFileAsync(fileId.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One loop must not take down its siblings. The file keeps its
                // lease until it expires, then another pass retries it.
                _logger.LogError(
                    exception,
                    "Old User Data upload loop failed on file {FileId}.",
                    fileId.Value
                );
            }
        }
    }

    private async Task<string?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OldUserDataImportJobs
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => x.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Reserves the next available file in a single statement. FOR UPDATE SKIP
    /// LOCKED lets concurrent workers walk past rows another worker is already
    /// claiming instead of blocking on them.
    /// </summary>
    private async Task<Guid?> ClaimNextFileAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var leaseUntil = now.AddMinutes(Math.Max(1, _options.LeaseMinutes));

        var claimed = await db.Database
            .SqlQueryRaw<Guid>(
                """
                UPDATE "OldUserDataFiles" AS target
                SET "Status" = 'uploading',
                    "AttemptCount" = target."AttemptCount" + 1,
                    "ErrorMessage" = '',
                    "LeaseUntilUtc" = {1},
                    "UploadStartedAtUtc" = COALESCE(target."UploadStartedAtUtc", {2})
                WHERE target."Id" = (
                    SELECT candidate."Id"
                    FROM "OldUserDataFiles" AS candidate
                    WHERE candidate."ImportJobId" = {0}
                      AND (candidate."Status" = 'indexed' OR candidate."Status" = 'uploading')
                      AND (candidate."LeaseUntilUtc" IS NULL OR candidate."LeaseUntilUtc" < {2})
                    ORDER BY candidate."UserFolder", candidate."RelativePath"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                RETURNING target."Id"
                """,
                jobId,
                leaseUntil,
                now)
            .ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }

    private async Task<bool> HasClaimableFilesAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OldUserDataFiles
            .AsNoTracking()
            .AnyAsync(
                x => x.ImportJobId == jobId &&
                     (x.Status == "indexed" || x.Status == "uploading"),
                cancellationToken
            );
    }

    private async Task UploadOneFileAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        OldUserDataFile file;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // ClaimNextFileAsync already set the status, attempt count and lease in
            // the same statement that reserved this row, so only read it here.
            file = await db.OldUserDataFiles
                .AsNoTracking()
                .FirstAsync(x => x.Id == fileId, cancellationToken);
        }

        try
        {
            var currentInfo = new FileInfo(file.FullPath);
            if (!currentInfo.Exists)
            {
                await MarkFileFailedAsync(file, "missing", "Source file is missing.", cancellationToken);
                return;
            }

            if (currentInfo.Length != file.SizeBytes ||
                !TimestampsMatch(currentInfo.LastWriteTimeUtc, file.UpdatedAtUtc))
            {
                await MarkFileFailedAsync(
                    file,
                    "failed",
                    "Source file changed after the metadata scan.",
                    cancellationToken
                );
                return;
            }

            if (string.IsNullOrWhiteSpace(file.MultipartUploadId))
            {
                var multipart = await _storage.StartMultipartUploadAsync(
                    file.ObjectKey,
                    file.ContentType,
                    cancellationToken
                );

                file.MultipartUploadId = multipart.UploadId;

                await using var startScope = _scopeFactory.CreateAsyncScope();
                var startDb = startScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await startDb.OldUserDataFiles
                    .Where(x => x.Id == file.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.MultipartUploadId, multipart.UploadId)
                        .SetProperty(x => x.ObjectKey, multipart.ObjectKey),
                        cancellationToken);
            }

            IReadOnlyList<UploadedPartInfo> uploadedParts;

            try
            {
                uploadedParts = await _storage.ListUploadedPartsAsync(
                    file.ObjectKey,
                    file.MultipartUploadId,
                    cancellationToken
                );
            }
            catch (AmazonS3Exception exception) when (IsMissingUpload(exception))
            {
                var multipart = await _storage.StartMultipartUploadAsync(
                    file.ObjectKey,
                    file.ContentType,
                    cancellationToken
                );
                file.MultipartUploadId = multipart.UploadId;
                uploadedParts = [];

                await using var restartScope = _scopeFactory.CreateAsyncScope();
                var restartDb = restartScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await restartDb.OldUserDataFiles
                    .Where(x => x.Id == file.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.MultipartUploadId, multipart.UploadId)
                        .SetProperty(x => x.UploadedBytes, 0),
                        cancellationToken);
            }

            var parts = uploadedParts.ToDictionary(x => x.PartNumber);
            var expectedPartCount = checked((int)Math.Ceiling(
                file.SizeBytes / (double)file.PartSizeBytes
            ));

            var pendingParts = Enumerable
                .Range(1, expectedPartCount)
                .Where(partNumber => !parts.ContainsKey(partNumber))
                .ToArray();

            if (pendingParts.Length > 0)
            {
                // One TCP stream to Backblaze settles around 0.2 MB/s on this link
                // regardless of file size, so large files are only fast when several
                // parts are in flight together.
                var partConcurrency = Math.Clamp(_options.PartConcurrency, 1, 16);
                using var partLimit = new SemaphoreSlim(partConcurrency, partConcurrency);
                var completedParts = new System.Collections.Concurrent.ConcurrentDictionary<
                    int, UploadedPartInfo>(parts);

                var partTasks = pendingParts.Select(async partNumber =>
                {
                    await partLimit.WaitAsync(cancellationToken);
                    try
                    {
                        var offset = (partNumber - 1L) * file.PartSizeBytes;
                        var length = Math.Min(file.PartSizeBytes, file.SizeBytes - offset);
                        var uploadedPart = await UploadPartWithRetryAsync(
                            file,
                            partNumber,
                            offset,
                            length,
                            cancellationToken
                        );
                        completedParts[partNumber] = uploadedPart;

                        // Doubles as a lease renewal so a slow multi-part file does
                        // not lose its claim part way through.
                        await UpdateFileProgressAsync(
                            file.Id,
                            completedParts.Values.Sum(x => x.SizeBytes),
                            cancellationToken
                        );
                    }
                    finally
                    {
                        partLimit.Release();
                    }
                });

                await Task.WhenAll(partTasks);

                parts = completedParts.ToDictionary(x => x.Key, x => x.Value);
            }

            currentInfo.Refresh();
            if (!currentInfo.Exists ||
                currentInfo.Length != file.SizeBytes ||
                !TimestampsMatch(currentInfo.LastWriteTimeUtc, file.UpdatedAtUtc))
            {
                throw new IOException("Source file changed while it was uploading.");
            }

            var sourceSha256 = await CalculateSha256Async(
                file.FullPath,
                cancellationToken
            );
            currentInfo.Refresh();
            if (!currentInfo.Exists ||
                currentInfo.Length != file.SizeBytes ||
                !TimestampsMatch(currentInfo.LastWriteTimeUtc, file.UpdatedAtUtc))
            {
                throw new IOException("Source file changed while its checksum was being calculated.");
            }

            var completedObject = await _storage.CompleteMultipartUploadAsync(
                file.ObjectKey,
                file.MultipartUploadId,
                parts.Values.ToList(),
                cancellationToken
            );

            file.B2VersionId = completedObject.VersionId;
            file.ObjectETag = completedObject.ETag;
            file.Sha256 = sourceSha256;
            await MarkFileCompletedAsync(file, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTransientInfrastructureFailure(exception))
        {
            // The database or the network went away, which says nothing about this
            // file. Marking it 'failed' would be a lie that only a manual resume can
            // undo, so release it and let the next claim pick it up again.
            _logger.LogWarning(
                exception,
                "Releasing Old User Data file for retry after an infrastructure failure. File: {FilePath}",
                file.FullPath
            );
            await ReleaseFileForRetryAsync(file.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Old User Data file upload failed. File: {FilePath}",
                file.FullPath
            );
            await MarkFileFailedAsync(file, "failed", exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Distinguishes "the database or network is unreachable" from "this file cannot
    /// be uploaded". Only the latter deserves a terminal 'failed' status, because a
    /// failed row is never re-claimed without an explicit resume.
    /// </summary>
    private static bool IsTransientInfrastructureFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is NpgsqlException or SocketException or TimeoutException)
            {
                return true;
            }

            // EF wraps connection drops in this, with the real cause underneath.
            if (current is InvalidOperationException &&
                current.Message.Contains("transient failure", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts a claimed file back in the queue without touching the job's failure
    /// counters, so an outage does not inflate the failed count or need a resume.
    /// </summary>
    private async Task ReleaseFileForRetryAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OldUserDataFiles
                .Where(x => x.Id == fileId && x.Status != "completed")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "indexed")
                    .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database is still down. The lease expiry is the backstop here.
            _logger.LogWarning(
                exception,
                "Could not release file {FileId} for retry; its lease will expire instead.",
                fileId
            );
        }
    }

    private async Task<UploadedPartInfo> UploadPartWithRetryAsync(
        OldUserDataFile file,
        int partNumber,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        var retryCount = Math.Max(1, _options.UploadRetryCount);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                return await _storage.UploadPartFromFileAsync(
                    file.ObjectKey,
                    file.MultipartUploadId,
                    partNumber,
                    file.FullPath,
                    offset,
                    length,
                    cancellationToken
                );
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && attempt < retryCount)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException(
            $"Part {partNumber} failed after {retryCount} attempts."
        );
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

    /// <summary>
    /// Records how far a file has uploaded and renews its lease. This is advisory
    /// bookkeeping - the parts are already safe in object storage - so a failure here
    /// must never abort an upload that is otherwise succeeding. A database blip used
    /// to propagate out of the part task and fail the whole file.
    /// </summary>
    private async Task UpdateFileProgressAsync(
        Guid fileId,
        long uploadedBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OldUserDataFiles
                .Where(x => x.Id == fileId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.UploadedBytes, uploadedBytes)
                    // Renewing here keeps a long file from being stolen mid-upload.
                    .SetProperty(
                        x => x.LeaseUntilUtc,
                        DateTime.UtcNow.AddMinutes(Math.Max(1, _options.LeaseMinutes))
                    ),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Worst case the lease lapses and another pass re-claims the file, which
            // is the same recovery path an unclean shutdown already takes.
            _logger.LogWarning(
                exception,
                "Could not record upload progress for file {FileId}. Continuing.",
                fileId
            );
        }
    }

    private async Task MarkFileCompletedAsync(
        OldUserDataFile file,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var extractionQueue = scope.ServiceProvider
            .GetRequiredService<DocumentExtractionQueueService>();
        var completedAtUtc = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var transitionedToCompleted = await db.OldUserDataFiles
            .Where(x => x.Id == file.Id && x.Status != "completed")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "completed")
                .SetProperty(x => x.UploadedBytes, file.SizeBytes)
                .SetProperty(x => x.MultipartUploadId, "")
                .SetProperty(x => x.B2VersionId, file.B2VersionId)
                .SetProperty(x => x.ObjectETag, file.ObjectETag)
                .SetProperty(x => x.Sha256, file.Sha256)
                .SetProperty(x => x.ErrorMessage, "")
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.CompletedAtUtc, completedAtUtc),
                cancellationToken);

        if (transitionedToCompleted == 1)
        {
            await db.OldUserDataImportJobs
                .Where(x => x.Id == file.ImportJobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        x => x.UploadedFileCount,
                        x => x.UploadedFileCount + 1
                    )
                    .SetProperty(
                        x => x.UploadedSizeBytes,
                        x => x.UploadedSizeBytes + file.SizeBytes
                    )
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken);
        }
        else
        {
            var refreshedCompletedRow = await db.OldUserDataFiles
                .Where(x => x.Id == file.Id && x.Status == "completed")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.UploadedBytes, file.SizeBytes)
                    .SetProperty(x => x.MultipartUploadId, "")
                    .SetProperty(x => x.B2VersionId, file.B2VersionId)
                    .SetProperty(x => x.ObjectETag, file.ObjectETag)
                    .SetProperty(x => x.Sha256, file.Sha256)
                    .SetProperty(x => x.ErrorMessage, ""),
                    cancellationToken);

            if (refreshedCompletedRow != 1)
            {
                throw new InvalidOperationException(
                    $"Old User Data file {file.Id} no longer exists."
                );
            }
        }

        file.Status = "completed";
        file.UploadedBytes = file.SizeBytes;
        file.MultipartUploadId = "";
        file.ErrorMessage = "";
        file.Sha256 = file.Sha256.Trim().ToLowerInvariant();
        file.CompletedAtUtc = completedAtUtc;

        await extractionQueue.EnsureOldUserDataFileQueuedAsync(
            file,
            cancellationToken
        );
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MarkFileFailedAsync(
        OldUserDataFile file,
        string status,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.OldUserDataFiles
            .Where(x => x.Id == file.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                // Releasing the lease matters here: a failed row is no longer
                // claimable by status, so a stale lease would only mislead.
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.ErrorMessage, Truncate(errorMessage, 4000)),
                cancellationToken);

        await db.OldUserDataImportJobs
            .Where(x => x.Id == file.ImportJobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FailedFileCount, x => x.FailedFileCount + 1)
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasErrors = await db.OldUserDataFiles.AnyAsync(
            x => x.ImportJobId == jobId && (x.Status == "failed" || x.Status == "missing"),
            cancellationToken
        );

        await db.OldUserDataImportJobs
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, hasErrors ? "completed_with_errors" : "completed")
                .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    private async Task MarkJobFailedAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OldUserDataImportJobs
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.ErrorMessage, Truncate(errorMessage, 4000))
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    private async Task PauseJobForStorageFailureAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OldUserDataImportJobs
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "paused")
                .SetProperty(
                    x => x.ErrorMessage,
                    "Object storage health check failed: " + Truncate(errorMessage, 3900)
                )
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    private async Task<OldUserDataImportJob?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OldUserDataImportJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
    }

    private async Task<bool> IsJobPausedAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OldUserDataImportJobs
            .AnyAsync(x => x.Id == jobId && x.Status == "paused", cancellationToken);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(
        string rootPath,
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
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var childFolder in childFolders)
            {
                try
                {
                    var attributes = File.GetAttributes(childFolder);
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        folders.Push(childFolder);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip unreadable folders and continue the one-time scan.
                }
                catch (IOException)
                {
                    // Skip unavailable folders and continue the one-time scan.
                }
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(folder);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(path);
                    if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                yield return fileInfo;
            }
        }
    }

    private string CreateObjectKey(string userFolder, string relativePath)
    {
        var prefix = _options.ObjectKeyPrefix.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "uploads/old-user-data";
        }

        var safeUserFolder = SanitizePathSegment(userFolder);
        var safeRelativePath = string.Join(
            '/',
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x != "." && x != "..")
                .Select(SanitizePathSegment)
        );

        return $"{prefix}/{safeUserFolder}/{safeRelativePath}";
    }

    private static string CreateDeviceCode(string userFolder)
    {
        var value = new string(userFolder.Trim().ToUpperInvariant()
            .Select(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.' ? x : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(value) ? "OLD_USER" : value;
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = new string(value.Trim()
            .Select(x => char.IsControl(x) || x is '/' or '\\' ? '_' : x)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().TrimStart('.');
        return extension.Length == 0 ? "" : "." + extension.ToLowerInvariant();
    }

    private static string GetContentType(string extension)
    {
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".doc" or ".dot" => "application/msword",
            ".docx" or ".dotx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" or ".xlt" => "application/vnd.ms-excel",
            ".xlsx" or ".xltx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv" => "text/csv",
            ".ppt" or ".pps" or ".pot" => "application/vnd.ms-powerpoint",
            ".pptx" or ".ppsx" or ".potx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".rtf" => "application/rtf",
            _ => "application/octet-stream"
        };
    }

    private static bool IsMissingUpload(AmazonS3Exception exception)
    {
        return exception.StatusCode == System.Net.HttpStatusCode.NotFound ||
               string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static bool TimestampsMatch(DateTime left, DateTime right)
    {
        return Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).Ticks) <
               TimeSpan.TicksPerMillisecond;
    }
}
