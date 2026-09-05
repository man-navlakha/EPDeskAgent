using EPDeskServerApi.Data;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;

namespace EPDeskMcpServer.Services;

/// <summary>
/// A document only stores the id of the ingestion row it came from, but the
/// original Windows path is the thing people actually recognise. This resolves
/// those paths in two queries per page rather than one query per document.
/// </summary>
public static class SourcePathResolver
{
    public static async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        AppDbContext db,
        IReadOnlyCollection<(string SourceType, Guid SourceRecordId)> sources,
        CancellationToken cancellationToken)
    {
        var paths = new Dictionary<Guid, string>();

        if (sources.Count == 0)
        {
            return paths;
        }

        var automaticIds = sources
            .Where(x => x.SourceType ==
                DocumentExtractionQueueService.AutomaticUploadSourceType)
            .Select(x => x.SourceRecordId)
            .Distinct()
            .ToList();

        var oldUserDataIds = sources
            .Where(x => x.SourceType ==
                DocumentExtractionQueueService.OldUserDataSourceType)
            .Select(x => x.SourceRecordId)
            .Distinct()
            .ToList();

        if (automaticIds.Count > 0)
        {
            var rows = await db.AutomaticFileUploads
                .AsNoTracking()
                .Where(u => automaticIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullPath })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                paths[row.Id] = row.FullPath;
            }
        }

        if (oldUserDataIds.Count > 0)
        {
            var rows = await db.OldUserDataFiles
                .AsNoTracking()
                .Where(f => oldUserDataIds.Contains(f.Id))
                .Select(f => new { f.Id, f.FullPath })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                paths[row.Id] = row.FullPath;
            }
        }

        return paths;
    }

    public static string Lookup(
        IReadOnlyDictionary<Guid, string> paths,
        Guid sourceRecordId)
    {
        return paths.TryGetValue(sourceRecordId, out var path) ? path : "";
    }
}
