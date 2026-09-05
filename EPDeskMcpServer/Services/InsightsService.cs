using EPDeskMcpServer.Contracts;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Fleet-level and pipeline-level views: which machines report in, what is
/// still uploading, what failed to extract, and how the corpus breaks down.
/// </summary>
public sealed class InsightsService
{
    private const int MaxGroupRows = 25;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public InsightsService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PagedResult<DeviceSummary>> ListDevicesAsync(
        string? status,
        bool includeInactive,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var devices = db.Devices.AsNoTracking();

        if (!includeInactive)
        {
            devices = devices.Where(d => d.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            devices = devices.Where(d => d.Status == normalized);
        }

        var totalCount = await devices.CountAsync(cancellationToken);

        var rows = await devices
            .OrderByDescending(d => d.LastSeenAtUtc ?? d.RegisteredAtUtc)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var deviceCodes = rows.Select(r => r.DeviceCode).ToList();

        // Two grouped queries for the whole page rather than a pair of counts
        // per device, which keeps the tool responsive on a large fleet. They
        // stay separate because counting documents and summing version bytes
        // are aggregates over different grains.
        var documentCounts = await db.Documents
            .AsNoTracking()
            .Where(d => !d.IsDeleted && deviceCodes.Contains(d.DeviceCode))
            .GroupBy(d => d.DeviceCode)
            .Select(g => new { DeviceCode = g.Key, DocumentCount = g.Count() })
            .ToListAsync(cancellationToken);

        var byteTotals = await (
            from document in db.Documents.AsNoTracking()
            join version in db.DocumentVersions.AsNoTracking()
                on document.Id equals version.DocumentId
            where !document.IsDeleted && deviceCodes.Contains(document.DeviceCode)
            group version by document.DeviceCode into deviceGroup
            select new
            {
                DeviceCode = deviceGroup.Key,
                SizeBytes = deviceGroup.Sum(v => (long?)v.SizeBytes) ?? 0L
            }
        ).ToListAsync(cancellationToken);

        var countsByDevice = documentCounts.ToDictionary(
            x => x.DeviceCode,
            x => x.DocumentCount
        );

        var bytesByDevice = byteTotals.ToDictionary(
            x => x.DeviceCode,
            x => x.SizeBytes
        );

        var items = rows
            .Select(device => new DeviceSummary(
                device.DeviceCode,
                device.Nickname,
                device.Hostname,
                device.Username,
                device.AgentVersion,
                device.Status,
                device.IsActive,
                device.LastSeenAtUtc,
                device.RegisteredAtUtc,
                countsByDevice.GetValueOrDefault(device.DeviceCode),
                bytesByDevice.GetValueOrDefault(device.DeviceCode)
            ))
            .ToList();

        return new PagedResult<DeviceSummary>(
            totalCount,
            offset,
            limit,
            items.Count,
            offset + items.Count < totalCount ? offset + items.Count : null,
            items
        );
    }

    public async Task<PagedResult<IngestionRecord>> ListIngestionAsync(
        string? sourceType,
        string? deviceCode,
        string? status,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var normalizedSource = string.IsNullOrWhiteSpace(sourceType)
            ? null
            : sourceType.Trim().ToLowerInvariant();

        var normalizedDevice = string.IsNullOrWhiteSpace(deviceCode)
            ? null
            : deviceCode.Trim().ToUpperInvariant();

        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? null
            : status.Trim().ToLowerInvariant();

        if (normalizedSource is not null &&
            normalizedSource != DocumentExtractionQueueService.AutomaticUploadSourceType &&
            normalizedSource != DocumentExtractionQueueService.OldUserDataSourceType)
        {
            throw new McpToolException(
                $"Unknown sourceType '{normalizedSource}'. Use " +
                $"'{DocumentExtractionQueueService.AutomaticUploadSourceType}', " +
                $"'{DocumentExtractionQueueService.OldUserDataSourceType}', or " +
                "omit it to see both."
            );
        }

        var records = new List<IngestionRecord>();
        var totalCount = 0;

        if (normalizedSource is null or
            DocumentExtractionQueueService.AutomaticUploadSourceType)
        {
            var uploads = db.AutomaticFileUploads.AsNoTracking();

            if (normalizedDevice is not null)
            {
                uploads = uploads.Where(u => u.DeviceCode == normalizedDevice);
            }

            if (normalizedStatus is not null)
            {
                uploads = uploads.Where(u => u.Status == normalizedStatus);
            }

            totalCount += await uploads.CountAsync(cancellationToken);

            var rows = await uploads
                .OrderByDescending(u => u.UpdatedAtUtc)
                .Take(offset + limit)
                .Select(u => new IngestionRecord(
                    DocumentExtractionQueueService.AutomaticUploadSourceType,
                    u.Id,
                    null,
                    u.DeviceCode,
                    u.FileName,
                    u.Extension,
                    u.FullPath,
                    u.SizeBytes,
                    u.Status,
                    u.ErrorMessage,
                    u.UpdatedAtUtc,
                    u.CompletedAtUtc
                ))
                .ToListAsync(cancellationToken);

            records.AddRange(rows);
        }

        if (normalizedSource is null or
            DocumentExtractionQueueService.OldUserDataSourceType)
        {
            var files = db.OldUserDataFiles.AsNoTracking();

            if (normalizedDevice is not null)
            {
                files = files.Where(f => f.DeviceCode == normalizedDevice);
            }

            if (normalizedStatus is not null)
            {
                files = files.Where(f => f.Status == normalizedStatus);
            }

            totalCount += await files.CountAsync(cancellationToken);

            var rows = await files
                .OrderByDescending(f => f.CompletedAtUtc ?? f.IndexedAtUtc)
                .Take(offset + limit)
                .Select(f => new IngestionRecord(
                    DocumentExtractionQueueService.OldUserDataSourceType,
                    f.Id,
                    null,
                    f.DeviceCode,
                    f.FileName,
                    f.Extension,
                    f.FullPath,
                    f.SizeBytes,
                    f.Status,
                    f.ErrorMessage,
                    f.CompletedAtUtc ?? f.IndexedAtUtc,
                    f.CompletedAtUtc
                ))
                .ToListAsync(cancellationToken);

            records.AddRange(rows);
        }

        var page = records
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Skip(offset)
            .Take(limit)
            .ToList();

        var withDocumentIds = await AttachDocumentIdsAsync(
            db,
            page,
            cancellationToken
        );

        return new PagedResult<IngestionRecord>(
            totalCount,
            offset,
            limit,
            withDocumentIds.Count,
            offset + withDocumentIds.Count < totalCount
                ? offset + withDocumentIds.Count
                : null,
            withDocumentIds
        );
    }

    public async Task<ExtractionOverview> GetExtractionOverviewAsync(
        int failureLimit,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var jobsByStatus = await db.ExtractionJobs
            .AsNoTracking()
            .GroupBy(j => j.Status)
            .Select(g => new CountByKey(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var versionsByStatus = await db.DocumentVersions
            .AsNoTracking()
            .GroupBy(v => v.ExtractionStatus)
            .Select(g => new CountByKey(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var failures = await db.ExtractionJobs
            .AsNoTracking()
            .Where(j =>
                j.Status == "dead_letter" ||
                j.Status == "retry_wait" ||
                j.ErrorCode != ""
            )
            .OrderByDescending(j => j.UpdatedAtUtc)
            .Take(failureLimit)
            .Select(j => new ExtractionFailure(
                j.DocumentVersionId,
                j.DocumentVersion.DocumentId,
                j.DocumentVersion.FileName,
                j.Status,
                j.AttemptCount,
                j.MaxAttempts,
                j.ErrorCode,
                j.ErrorMessage,
                j.NextAttemptAtUtc,
                j.UpdatedAtUtc
            ))
            .ToListAsync(cancellationToken);

        return new ExtractionOverview(
            jobsByStatus.OrderByDescending(x => x.Count).ToList(),
            versionsByStatus.OrderByDescending(x => x.Count).ToList(),
            jobsByStatus.FirstOrDefault(x => x.Key == "queued")?.Count ?? 0,
            jobsByStatus.FirstOrDefault(x => x.Key == "running")?.Count ?? 0,
            jobsByStatus.FirstOrDefault(x => x.Key == "dead_letter")?.Count ?? 0,
            failures
        );
    }

    public async Task<StorageStats> GetStorageStatsAsync(
        DocumentFilter filter,
        CancellationToken cancellationToken)
    {
        filter.Validate();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var documents = filter.Apply(db.Documents.AsNoTracking(), db);

        var documentCount = await documents.CountAsync(cancellationToken);

        var versions = db.DocumentVersions
            .AsNoTracking()
            .Where(v => documents.Any(d => d.Id == v.DocumentId));

        var versionAggregate = await versions
            .GroupBy(_ => 1)
            .Select(g => new
            {
                VersionCount = g.Count(),
                TotalSizeBytes = g.Sum(v => (long?)v.SizeBytes) ?? 0L
            })
            .FirstOrDefaultAsync(cancellationToken);

        var versionCount = versionAggregate?.VersionCount ?? 0;

        // Every breakdown aggregates the same document-to-version join at the
        // version grain. Counting distinct documents inside a GROUP BY is not
        // translatable by EF Core, and counting stored files is the more
        // useful number for a storage summary anyway.
        var files = from document in documents
                    join version in db.DocumentVersions.AsNoTracking()
                        on document.Id equals version.DocumentId
                    select new
                    {
                        document.DeviceCode,
                        document.Department,
                        document.SourceType,
                        version.FileExtension,
                        version.SizeBytes
                    };

        var byExtension = await GroupFilesAsync(
            files.Select(f => new GroupInput
            {
                Key = f.FileExtension,
                SizeBytes = f.SizeBytes
            }),
            "(none)",
            cancellationToken
        );

        var byDevice = await GroupFilesAsync(
            files.Select(f => new GroupInput
            {
                Key = f.DeviceCode,
                SizeBytes = f.SizeBytes
            }),
            "(unknown)",
            cancellationToken
        );

        var byDepartment = await GroupFilesAsync(
            files.Select(f => new GroupInput
            {
                Key = f.Department,
                SizeBytes = f.SizeBytes
            }),
            "(unassigned)",
            cancellationToken
        );

        var bySourceType = await GroupFilesAsync(
            files.Select(f => new GroupInput
            {
                Key = f.SourceType,
                SizeBytes = f.SizeBytes
            }),
            "(unknown)",
            cancellationToken
        );

        return new StorageStats(
            documentCount,
            versionCount,
            versionAggregate?.TotalSizeBytes ?? 0,
            byExtension,
            byDevice,
            byDepartment,
            bySourceType
        );
    }

    /// <summary>
    /// Written with settable properties and used with object-initializer
    /// syntax on purpose: EF Core can see through a member initializer to the
    /// underlying column, but not through a positional record constructor, so
    /// a record here makes the GroupBy untranslatable.
    /// </summary>
    private sealed class GroupInput
    {
        public string Key { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    private static async Task<List<GroupStat>> GroupFilesAsync(
        IQueryable<GroupInput> files,
        string emptyKeyLabel,
        CancellationToken cancellationToken)
    {
        var rows = await files
            .GroupBy(f => f.Key)
            .Select(g => new
            {
                Key = g.Key,
                FileCount = g.Count(),
                SizeBytes = g.Sum(f => (long?)f.SizeBytes) ?? 0L
            })
            .OrderByDescending(x => x.SizeBytes)
            .Take(MaxGroupRows)
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new GroupStat(
                string.IsNullOrWhiteSpace(x.Key) ? emptyKeyLabel : x.Key,
                x.FileCount,
                x.SizeBytes
            ))
            .ToList();
    }

    /// <summary>
    /// Links each ingestion row back to the document it produced, so a model
    /// that finds a stuck upload can jump straight to the document tools.
    /// </summary>
    private static async Task<List<IngestionRecord>> AttachDocumentIdsAsync(
        AppDbContext db,
        List<IngestionRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return records;
        }

        var sourceRecordIds = records.Select(r => r.SourceRecordId).ToList();

        var links = await db.Documents
            .AsNoTracking()
            .Where(d => sourceRecordIds.Contains(d.SourceRecordId))
            .Select(d => new { d.SourceType, d.SourceRecordId, d.Id })
            .ToListAsync(cancellationToken);

        var byKey = links.ToDictionary(
            x => (x.SourceType, x.SourceRecordId),
            x => x.Id
        );

        return records
            .Select(r => byKey.TryGetValue(
                (r.SourceType, r.SourceRecordId),
                out var documentId)
                ? r with { DocumentId = documentId }
                : r)
            .ToList();
    }
}
