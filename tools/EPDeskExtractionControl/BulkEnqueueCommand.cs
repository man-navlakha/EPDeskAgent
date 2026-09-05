using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

internal static class BulkEnqueueCommand
{
    private const long MaximumSourceSizeBytes = 25L * 1024 * 1024;

    private static readonly string[] AllowedExtensions =
    [
        ".pdf",
        ".docx",
        ".pptx",
        ".txt",
        ".md"
    ];

    public static async Task<int> RunAsync(
        DbContextOptions<AppDbContext> dbOptions,
        IOptions<B2StorageOptions> storageOptions,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var totals = new BulkEnqueueTotals();

        Console.WriteLine(
            $"bulk_start batch_size={batchSize} max_size_bytes={MaximumSourceSizeBytes}"
        );

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchNumber = totals.BatchesCommitted + 1;
                var preferAutomatic = batchNumber % 2 != 0;
                var largerQuota = (batchSize + 1) / 2;
                var smallerQuota = batchSize / 2;
                var automaticQuota = preferAutomatic ? largerQuota : smallerQuota;
                var oldUserDataQuota = preferAutomatic ? smallerQuota : largerQuota;

                await using var db = new AppDbContext(dbOptions);
                await using var transaction =
                    await db.Database.BeginTransactionAsync(cancellationToken);

                var automaticUploads = await ReadAutomaticBatchAsync(
                    db,
                    automaticQuota,
                    cancellationToken
                );
                var oldUserDataFiles = await ReadOldUserDataBatchAsync(
                    db,
                    oldUserDataQuota,
                    cancellationToken
                );

                // If one source is exhausted, use its unused quota for the
                // other source while preserving alternating fairness whenever
                // both sources still have work.
                if (automaticUploads.Count < automaticQuota)
                {
                    oldUserDataFiles = await ReadOldUserDataBatchAsync(
                        db,
                        batchSize - automaticUploads.Count,
                        cancellationToken
                    );
                }
                else if (oldUserDataFiles.Count < oldUserDataQuota)
                {
                    automaticUploads = await ReadAutomaticBatchAsync(
                        db,
                        batchSize - oldUserDataFiles.Count,
                        cancellationToken
                    );
                }

                var selectedCount =
                    automaticUploads.Count + oldUserDataFiles.Count;
                if (selectedCount == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    break;
                }

                var queue = new DocumentExtractionQueueService(
                    db,
                    storageOptions
                );
                var batchCounters = new BulkEnqueueCounters();

                foreach (var upload in automaticUploads)
                {
                    var result = await queue.EnsureAutomaticUploadQueuedAsync(
                        upload,
                        cancellationToken
                    );
                    batchCounters.Add(result);
                }

                foreach (var file in oldUserDataFiles)
                {
                    var result = await queue.EnsureOldUserDataFileQueuedAsync(
                        file,
                        cancellationToken
                    );
                    batchCounters.Add(result);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                totals.Add(
                    automaticUploads.Count,
                    oldUserDataFiles.Count,
                    batchCounters
                );

                Console.WriteLine(
                    $"bulk_progress batch={totals.BatchesCommitted} " +
                    $"automatic={automaticUploads.Count} " +
                    $"old_user_data={oldUserDataFiles.Count} " +
                    $"selected={selectedCount} " +
                    $"jobs_created={batchCounters.JobsCreated} " +
                    $"already_queued={batchCounters.AlreadyQueued} " +
                    $"total_selected={totals.SourcesSelected} " +
                    $"total_jobs_created={totals.JobsCreated}"
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"bulk_cancelled batches_committed={totals.BatchesCommitted} " +
                $"total_selected={totals.SourcesSelected} " +
                $"total_jobs_created={totals.JobsCreated}"
            );
            return 130;
        }
        catch (Exception exception)
        {
            // Do not emit source IDs, object keys, paths, or exception messages.
            // The current transaction is disposed without commit and a rerun
            // resumes from the last successfully committed batch.
            Console.Error.WriteLine(
                $"bulk_failed batch={totals.BatchesCommitted + 1} " +
                $"error_type={exception.GetType().Name} " +
                $"batches_committed={totals.BatchesCommitted} " +
                $"total_selected={totals.SourcesSelected} " +
                $"total_jobs_created={totals.JobsCreated}"
            );
            return 4;
        }

        Console.WriteLine(
            $"bulk_complete batches_committed={totals.BatchesCommitted} " +
            $"automatic={totals.AutomaticSources} " +
            $"old_user_data={totals.OldUserDataSources} " +
            $"total_selected={totals.SourcesSelected} " +
            $"documents_created={totals.DocumentsCreated} " +
            $"versions_created={totals.VersionsCreated} " +
            $"jobs_created={totals.JobsCreated} " +
            $"already_queued={totals.AlreadyQueued}"
        );
        return 0;
    }

    private static Task<List<AutomaticFileUpload>> ReadAutomaticBatchAsync(
        AppDbContext db,
        int take,
        CancellationToken cancellationToken
    )
    {
        if (take == 0)
        {
            return Task.FromResult(new List<AutomaticFileUpload>());
        }

        return EligibleAutomaticUploads(db)
            .OrderBy(upload => upload.SizeBytes)
            .ThenBy(upload => upload.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static Task<List<OldUserDataFile>> ReadOldUserDataBatchAsync(
        AppDbContext db,
        int take,
        CancellationToken cancellationToken
    )
    {
        if (take == 0)
        {
            return Task.FromResult(new List<OldUserDataFile>());
        }

        return EligibleOldUserDataFiles(db)
            .OrderBy(file => file.SizeBytes)
            .ThenBy(file => file.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<AutomaticFileUpload> EligibleAutomaticUploads(
        AppDbContext db
    )
    {
        return db.AutomaticFileUploads
            .AsNoTracking()
            .Where(upload =>
                upload.Status == "completed" &&
                upload.SizeBytes >= 1 &&
                upload.SizeBytes <= MaximumSourceSizeBytes &&
                AllowedExtensions.Contains(upload.Extension.ToLower()) &&
                !db.Documents.Any(document =>
                    document.SourceType ==
                        DocumentExtractionQueueService.AutomaticUploadSourceType &&
                    document.SourceRecordId == upload.Id &&
                    document.Versions.Any(version =>
                        version.ObjectKey == upload.ObjectKey &&
                        version.SizeBytes == upload.SizeBytes &&
                        version.SourceModifiedAtUtc == upload.LastModifiedAtUtc &&
                        (upload.B2VersionId == "" ||
                         version.B2VersionId == upload.B2VersionId) &&
                        (upload.ObjectETag == "" ||
                         version.ObjectETag == upload.ObjectETag) &&
                        (upload.Sha256 == "" ||
                         version.Sha256.ToLower() == upload.Sha256.ToLower()) &&
                        version.ExtractionJobs.Any(job =>
                            job.PipelineVersion ==
                                DocumentExtractionQueueService.CurrentPipelineVersion
                        )
                    )
                )
            );
    }

    private static IQueryable<OldUserDataFile> EligibleOldUserDataFiles(
        AppDbContext db
    )
    {
        return db.OldUserDataFiles
            .AsNoTracking()
            .Where(file =>
                file.Status == "completed" &&
                file.SizeBytes >= 1 &&
                file.SizeBytes <= MaximumSourceSizeBytes &&
                AllowedExtensions.Contains(file.Extension.ToLower()) &&
                !db.Documents.Any(document =>
                    document.SourceType ==
                        DocumentExtractionQueueService.OldUserDataSourceType &&
                    document.SourceRecordId == file.Id &&
                    document.Versions.Any(version =>
                        version.ObjectKey == file.ObjectKey &&
                        version.SizeBytes == file.SizeBytes &&
                        version.SourceModifiedAtUtc == file.UpdatedAtUtc &&
                        (file.B2VersionId == "" ||
                         version.B2VersionId == file.B2VersionId) &&
                        (file.ObjectETag == "" ||
                         version.ObjectETag == file.ObjectETag) &&
                        (file.Sha256 == "" ||
                         version.Sha256.ToLower() == file.Sha256.ToLower()) &&
                        version.ExtractionJobs.Any(job =>
                            job.PipelineVersion ==
                                DocumentExtractionQueueService.CurrentPipelineVersion
                        )
                    )
                )
            );
    }

    private sealed class BulkEnqueueCounters
    {
        public int DocumentsCreated { get; private set; }

        public int VersionsCreated { get; private set; }

        public int JobsCreated { get; private set; }

        public int AlreadyQueued { get; private set; }

        public void Add(DocumentQueueResult result)
        {
            DocumentsCreated += result.DocumentCreated ? 1 : 0;
            VersionsCreated += result.VersionCreated ? 1 : 0;
            JobsCreated += result.JobCreated ? 1 : 0;
            AlreadyQueued += result.JobCreated ? 0 : 1;
        }
    }

    private sealed class BulkEnqueueTotals
    {
        public int BatchesCommitted { get; private set; }

        public long AutomaticSources { get; private set; }

        public long OldUserDataSources { get; private set; }

        public long SourcesSelected => AutomaticSources + OldUserDataSources;

        public long DocumentsCreated { get; private set; }

        public long VersionsCreated { get; private set; }

        public long JobsCreated { get; private set; }

        public long AlreadyQueued { get; private set; }

        public void Add(
            int automaticSources,
            int oldUserDataSources,
            BulkEnqueueCounters counters
        )
        {
            BatchesCommitted++;
            AutomaticSources += automaticSources;
            OldUserDataSources += oldUserDataSources;
            DocumentsCreated += counters.DocumentsCreated;
            VersionsCreated += counters.VersionsCreated;
            JobsCreated += counters.JobsCreated;
            AlreadyQueued += counters.AlreadyQueued;
        }
    }
}
