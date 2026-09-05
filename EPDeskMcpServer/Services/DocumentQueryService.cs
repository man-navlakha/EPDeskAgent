using EPDeskMcpServer.Configuration;
using EPDeskMcpServer.Contracts;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Read paths over the document catalogue: full-text search, browsing, and
/// paged access to extracted text.
/// </summary>
public sealed class DocumentQueryService
{
    /// <summary>
    /// Must match the text-search configuration used by the generated tsvector
    /// column in <c>DocumentExtractionModelConfiguration</c>. A mismatch here
    /// silently returns nothing, so the two values are deliberately coupled.
    /// </summary>
    private const string SearchConfiguration = "simple";

    private const int MaxSectionsPerContentRead = 120;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EpDeskMcpOptions _options;

    public DocumentQueryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<EpDeskMcpOptions> options)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
    }

    public async Task<SearchResult> SearchAsync(
        string query,
        DocumentFilter filter,
        int offset,
        int limit,
        bool groupByDocument,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpToolException(
                "A search query is required. To browse without a query, use " +
                "epdesk_list_documents instead."
            );
        }

        filter.Validate();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var matches = BuildSearchQuery(db, query, filter);

        var totalMatches = groupByDocument
            ? await matches.Select(x => x.DocumentId).Distinct()
                .CountAsync(cancellationToken)
            : await matches.CountAsync(cancellationToken);

        if (totalMatches == 0)
        {
            return new SearchResult(
                query,
                0,
                offset,
                limit,
                0,
                null,
                groupByDocument,
                []
            );
        }

        // Ranking and paging happen over a lightweight id+score projection so
        // the expensive text columns are only fetched for the page returned.
        var rankedIds = groupByDocument
            ? await RankGroupedAsync(matches, offset, limit, cancellationToken)
            : await RankFlatAsync(matches, offset, limit, cancellationToken);

        var hits = await MaterializeHitsAsync(
            db,
            rankedIds,
            query,
            cancellationToken
        );

        var nextOffset = offset + hits.Count < totalMatches
            ? offset + hits.Count
            : (int?)null;

        return new SearchResult(
            query,
            totalMatches,
            offset,
            limit,
            hits.Count,
            nextOffset,
            groupByDocument,
            hits
        );
    }

    public async Task<PagedResult<DocumentSummary>> ListDocumentsAsync(
        DocumentFilter filter,
        string sortBy,
        bool descending,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        filter.Validate();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var documents = filter.Apply(db.Documents.AsNoTracking(), db);

        var totalCount = await documents.CountAsync(cancellationToken);

        documents = ApplySort(documents, sortBy, descending);

        var rows = await documents
            .Skip(offset)
            .Take(limit)
            .Select(d => new
            {
                Document = d,
                VersionCount = d.Versions.Count,
                Latest = d.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new
                    {
                        v.Id,
                        v.VersionNumber,
                        v.FileName,
                        v.FileExtension,
                        v.SizeBytes,
                        v.ExtractionStatus,
                        v.SectionCount,
                        v.PageCount,
                        v.SourceModifiedAtUtc
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var paths = await SourcePathResolver.ResolveAsync(
            db,
            rows
                .Select(r => (r.Document.SourceType, r.Document.SourceRecordId))
                .ToList(),
            cancellationToken
        );

        var items = rows
            .Select(r => new DocumentSummary(
                r.Document.Id,
                r.Latest?.Id ?? Guid.Empty,
                r.Latest?.FileName ?? r.Document.DisplayName,
                r.Latest?.FileExtension ?? "",
                r.Latest?.SizeBytes ?? 0,
                r.Document.DeviceCode,
                r.Document.Department,
                r.Document.Classification,
                r.Document.SourceType,
                SourcePathResolver.Lookup(paths, r.Document.SourceRecordId),
                r.VersionCount,
                r.Latest?.VersionNumber ?? 0,
                r.Latest?.ExtractionStatus ?? "none",
                r.Latest?.SectionCount ?? 0,
                r.Latest?.PageCount,
                r.Latest?.SourceModifiedAtUtc,
                r.Document.UpdatedAtUtc
            ))
            .ToList();

        return new PagedResult<DocumentSummary>(
            totalCount,
            offset,
            limit,
            items.Count,
            offset + items.Count < totalCount ? offset + items.Count : null,
            items
        );
    }

    public async Task<DocumentDetail> GetDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var document = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            throw new McpToolException(
                $"No document found with id {documentId}. Use " +
                "epdesk_list_documents to find a valid documentId."
            );
        }

        var versions = await db.DocumentVersions
            .AsNoTracking()
            .Where(v => v.DocumentId == documentId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new DocumentVersionSummary(
                v.Id,
                v.VersionNumber,
                v.FileName,
                v.FileExtension,
                v.SizeBytes,
                v.ExtractionStatus,
                v.SectionCount,
                v.PageCount,
                v.ExtractionErrorCode,
                v.ExtractionError,
                v.SourceModifiedAtUtc,
                v.ExtractedAtUtc,
                v.Derivatives
                    .Select(x => x.Kind)
                    .Distinct()
                    .ToList()
            ))
            .ToListAsync(cancellationToken);

        var paths = await SourcePathResolver.ResolveAsync(
            db,
            [(document.SourceType, document.SourceRecordId)],
            cancellationToken
        );

        return new DocumentDetail(
            document.Id,
            document.DisplayName,
            document.SourceType,
            document.SourceRecordId,
            SourcePathResolver.Lookup(paths, document.SourceRecordId),
            document.DeviceCode,
            document.Department,
            document.Classification,
            document.IsDeleted,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            versions
        );
    }

    public async Task<DocumentContent> ReadContentAsync(
        Guid? documentId,
        Guid? versionId,
        int offset,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var version = await ResolveVersionAsync(
            db,
            documentId,
            versionId,
            cancellationToken
        );

        if (version.ExtractionStatus != "completed" && version.SectionCount == 0)
        {
            throw new McpToolException(
                $"No extracted text is available for '{version.FileName}'. " +
                $"Extraction status is '{version.ExtractionStatus}'" +
                (string.IsNullOrWhiteSpace(version.ExtractionError)
                    ? "."
                    : $": {version.ExtractionError}.") +
                " Call epdesk_get_file_metadata for detail, or " +
                "epdesk_get_download_url to fetch the original file instead."
            );
        }

        var pipelineVersion = await ResolvePipelineVersionAsync(
            db,
            version,
            cancellationToken
        );

        var sectionsQuery = db.DocumentSections
            .AsNoTracking()
            .Where(s =>
                s.DocumentVersionId == version.Id &&
                s.PipelineVersion == pipelineVersion
            );

        var totalSections = await sectionsQuery.CountAsync(cancellationToken);

        var candidates = await sectionsQuery
            .OrderBy(s => s.Ordinal)
            .Skip(offset)
            .Take(MaxSectionsPerContentRead)
            .Select(s => new
            {
                s.Ordinal,
                s.SectionType,
                s.SectionNumber,
                s.Heading,
                s.Content,
                s.CharacterCount
            })
            .ToListAsync(cancellationToken);

        var sections = new List<DocumentSectionContent>();
        var charactersReturned = 0;
        var truncated = false;

        foreach (var candidate in candidates)
        {
            var content = candidate.Content ?? "";

            // Always return at least one section, otherwise a single oversized
            // page would make the document permanently unreadable.
            if (sections.Count > 0 &&
                charactersReturned + content.Length > maxCharacters)
            {
                truncated = true;
                break;
            }

            if (charactersReturned + content.Length > maxCharacters)
            {
                var remaining = Math.Max(0, maxCharacters - charactersReturned);
                content = content[..Math.Min(content.Length, remaining)];
                truncated = true;
            }

            sections.Add(new DocumentSectionContent(
                candidate.Ordinal,
                candidate.SectionType,
                candidate.SectionNumber,
                candidate.Heading,
                content,
                candidate.CharacterCount
            ));

            charactersReturned += content.Length;

            if (truncated)
            {
                break;
            }
        }

        var consumed = offset + sections.Count;

        if (consumed < totalSections)
        {
            truncated = true;
        }

        var paths = await SourcePathResolver.ResolveAsync(
            db,
            [(version.SourceType, version.SourceRecordId)],
            cancellationToken
        );

        return new DocumentContent(
            version.DocumentId,
            version.Id,
            version.FileName,
            SourcePathResolver.Lookup(paths, version.SourceRecordId),
            version.ExtractionStatus,
            totalSections,
            offset,
            sections.Count,
            consumed < totalSections ? consumed : null,
            truncated,
            charactersReturned,
            sections
        );
    }

    /// <summary>
    /// Resolves the version a tool call refers to. Callers may name a version
    /// directly or name a document and get its newest version.
    /// </summary>
    internal static async Task<ResolvedVersion> ResolveVersionAsync(
        AppDbContext db,
        Guid? documentId,
        Guid? versionId,
        CancellationToken cancellationToken)
    {
        if (documentId is null && versionId is null)
        {
            throw new McpToolException(
                "Supply either documentId or versionId."
            );
        }

        var query = db.DocumentVersions
            .AsNoTracking()
            .Join(
                db.Documents.AsNoTracking(),
                v => v.DocumentId,
                d => d.Id,
                (v, d) => new ResolvedVersion
                {
                    Id = v.Id,
                    DocumentId = v.DocumentId,
                    VersionNumber = v.VersionNumber,
                    FileName = v.FileName,
                    FileExtension = v.FileExtension,
                    BucketName = v.BucketName,
                    ObjectKey = v.ObjectKey,
                    B2VersionId = v.B2VersionId,
                    ObjectETag = v.ObjectETag,
                    SizeBytes = v.SizeBytes,
                    Sha256 = v.Sha256,
                    DeclaredContentType = v.DeclaredContentType,
                    DetectedContentType = v.DetectedContentType,
                    SourceModifiedAtUtc = v.SourceModifiedAtUtc,
                    ExtractionStatus = v.ExtractionStatus,
                    ExtractionPipelineVersion = v.ExtractionPipelineVersion,
                    ExtractionMetadataJson = v.ExtractionMetadataJson,
                    ExtractionErrorCode = v.ExtractionErrorCode,
                    ExtractionError = v.ExtractionError,
                    ExtractedAtUtc = v.ExtractedAtUtc,
                    SectionCount = v.SectionCount,
                    PageCount = v.PageCount,
                    SourceType = d.SourceType,
                    SourceRecordId = d.SourceRecordId,
                    DeviceCode = d.DeviceCode,
                    Department = d.Department,
                    Classification = d.Classification
                }
            );

        ResolvedVersion? resolved;

        if (versionId is not null)
        {
            resolved = await query
                .FirstOrDefaultAsync(x => x.Id == versionId, cancellationToken);

            if (resolved is null)
            {
                throw new McpToolException(
                    $"No document version found with id {versionId}. " +
                    "epdesk_get_document lists the valid versionId values for " +
                    "a document."
                );
            }

            return resolved;
        }

        resolved = await query
            .Where(x => x.DocumentId == documentId)
            .OrderByDescending(x => x.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (resolved is null)
        {
            throw new McpToolException(
                $"Document {documentId} has no stored versions, so there is " +
                "nothing to read or download."
            );
        }

        return resolved;
    }

    private static async Task<string> ResolvePipelineVersionAsync(
        AppDbContext db,
        ResolvedVersion version,
        CancellationToken cancellationToken)
    {
        var pipelines = await db.DocumentSections
            .AsNoTracking()
            .Where(s => s.DocumentVersionId == version.Id)
            .GroupBy(s => s.PipelineVersion)
            .Select(g => new { Pipeline = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        if (pipelines.Count == 0)
        {
            return version.ExtractionPipelineVersion;
        }

        // Prefer the pipeline the version records as authoritative; fall back
        // to whichever run actually produced the most sections.
        var declared = pipelines.FirstOrDefault(p =>
            p.Pipeline == version.ExtractionPipelineVersion
        );

        return declared?.Pipeline
            ?? pipelines.OrderByDescending(p => p.Count).First().Pipeline;
    }

    private static IQueryable<SearchCandidate> BuildSearchQuery(
        AppDbContext db,
        string query,
        DocumentFilter filter)
    {
        var documents = filter.Apply(db.Documents.AsNoTracking(), db);

        return from section in db.DocumentSections.AsNoTracking()
               join version in db.DocumentVersions.AsNoTracking()
                   on section.DocumentVersionId equals version.Id
               join document in documents
                   on version.DocumentId equals document.Id
               where section.SearchVector.Matches(
                   EF.Functions.WebSearchToTsQuery(SearchConfiguration, query)
               )
               select new SearchCandidate
               {
                   SectionId = section.Id,
                   DocumentId = document.Id,
                   Score = section.SearchVector.Rank(
                       EF.Functions.WebSearchToTsQuery(SearchConfiguration, query)
                   )
               };
    }

    private static async Task<List<RankedSection>> RankFlatAsync(
        IQueryable<SearchCandidate> matches,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await matches
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SectionId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new RankedSection(x.SectionId, x.Score, 1))
            .ToList();
    }

    private static async Task<List<RankedSection>> RankGroupedAsync(
        IQueryable<SearchCandidate> matches,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        // One document can match on many sections. Collapsing to the best
        // section per document needs the candidate window read first, so the
        // window is sized generously relative to the requested page.
        var windowSize = Math.Min(2000, ((offset + limit) * 10) + 100);

        var candidates = await matches
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SectionId)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        return candidates
            .GroupBy(x => x.DocumentId)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.SectionId)
                    .First();

                return new RankedSection(
                    best.SectionId,
                    best.Score,
                    group.Count()
                );
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SectionId)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private static async Task<List<SearchHit>> MaterializeHitsAsync(
        AppDbContext db,
        List<RankedSection> ranked,
        string query,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var sectionIds = ranked.Select(x => x.SectionId).ToList();

        var rows = await (
            from section in db.DocumentSections.AsNoTracking()
            join version in db.DocumentVersions.AsNoTracking()
                on section.DocumentVersionId equals version.Id
            join document in db.Documents.AsNoTracking()
                on version.DocumentId equals document.Id
            where sectionIds.Contains(section.Id)
            select new
            {
                SectionId = section.Id,
                section.SectionType,
                section.SectionNumber,
                section.Heading,

                // Only the leading slice can be excerpted, which bounds the
                // transfer for pages whose extracted text is very large.
                Excerpt = section.Content.Substring(0, 4000),
                VersionId = version.Id,
                version.FileName,
                version.FileExtension,
                version.SourceModifiedAtUtc,
                DocumentId = document.Id,
                document.DeviceCode,
                document.Department,
                document.Classification,
                document.SourceType,
                document.SourceRecordId
            }
        ).ToListAsync(cancellationToken);

        var byId = rows.ToDictionary(x => x.SectionId);

        var highlights = await SectionHighlighter.BuildAsync(
            db,
            sectionIds,
            query,
            cancellationToken
        );

        var paths = await SourcePathResolver.ResolveAsync(
            db,
            rows.Select(r => (r.SourceType, r.SourceRecordId)).ToList(),
            cancellationToken
        );

        var hits = new List<SearchHit>(ranked.Count);

        foreach (var rank in ranked)
        {
            if (!byId.TryGetValue(rank.SectionId, out var row))
            {
                continue;
            }

            hits.Add(new SearchHit(
                row.DocumentId,
                row.VersionId,
                row.FileName,
                row.FileExtension,
                row.DeviceCode,
                row.Department,
                row.Classification,
                row.SourceType,
                SourcePathResolver.Lookup(paths, row.SourceRecordId),
                row.SectionType,
                row.SectionNumber,
                row.Heading,

                // ts_headline is authoritative; the trimmed excerpt only
                // covers the case where it finds nothing to mark up.
                highlights.TryGetValue(rank.SectionId, out var highlight)
                    ? highlight
                    : SnippetBuilder.Build(row.Excerpt, query),
                Math.Round(rank.Score, 6),
                rank.MatchingSectionCount,
                row.SourceModifiedAtUtc
            ));
        }

        return hits;
    }

    private static IQueryable<Document> ApplySort(
        IQueryable<Document> documents,
        string sortBy,
        bool descending)
    {
        return sortBy switch
        {
            "created" => descending
                ? documents.OrderByDescending(d => d.CreatedAtUtc)
                : documents.OrderBy(d => d.CreatedAtUtc),
            "name" => descending
                ? documents.OrderByDescending(d => d.DisplayName)
                : documents.OrderBy(d => d.DisplayName),
            "size" => descending
                ? documents.OrderByDescending(d =>
                    d.Versions.Max(v => (long?)v.SizeBytes) ?? 0)
                : documents.OrderBy(d =>
                    d.Versions.Max(v => (long?)v.SizeBytes) ?? 0),
            _ => descending
                ? documents.OrderByDescending(d => d.UpdatedAtUtc)
                : documents.OrderBy(d => d.UpdatedAtUtc)
        };
    }

    private sealed class SearchCandidate
    {
        public Guid SectionId { get; set; }
        public Guid DocumentId { get; set; }
        public float Score { get; set; }
    }

    private sealed record RankedSection(
        Guid SectionId,
        float Score,
        int MatchingSectionCount
    );
}

/// <summary>
/// Flattened version plus its owning document, which is what almost every
/// file-level tool needs.
/// </summary>
internal sealed class ResolvedVersion
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; }
    public string FileName { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string B2VersionId { get; set; } = "";
    public string ObjectETag { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string DeclaredContentType { get; set; } = "";
    public string DetectedContentType { get; set; } = "";
    public DateTime? SourceModifiedAtUtc { get; set; }
    public string ExtractionStatus { get; set; } = "";
    public string ExtractionPipelineVersion { get; set; } = "";
    public string ExtractionMetadataJson { get; set; } = "{}";
    public string ExtractionErrorCode { get; set; } = "";
    public string ExtractionError { get; set; } = "";
    public DateTime? ExtractedAtUtc { get; set; }
    public int SectionCount { get; set; }
    public int? PageCount { get; set; }
    public string SourceType { get; set; } = "";
    public Guid SourceRecordId { get; set; }
    public string DeviceCode { get; set; } = "";
    public string Department { get; set; } = "";
    public string Classification { get; set; } = "";
}
