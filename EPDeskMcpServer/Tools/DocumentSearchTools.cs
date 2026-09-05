using System.ComponentModel;
using EPDeskMcpServer.Contracts;
using EPDeskMcpServer.Services;
using ModelContextProtocol.Server;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// Finding documents by their metadata: filtered browsing over name, Windows
/// path, device, source, type, size, and modification time.
/// </summary>
[McpServerToolType]
public sealed class DocumentSearchTools
{
    [McpServerTool(
        Name = "epdesk_list_documents",
        Title = "Browse the document catalogue",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Browse and filter the document catalogue. This is the only way to find
        a file: use it to match a name or Windows path, to list everything from
        one device or department, or to find the largest or newest files.

        There is no full-text search. Nothing here looks inside a file, so a
        request that describes a document's contents cannot be answered by
        guessing at nameContains - narrow by path, device, extension, or date
        instead.

        Filters that describe a file (extension, size, modified date) match a
        document that has any version matching, so a document that changed over
        time is still found by its older state.

        Every result carries documentId and latestVersionId, which the metadata
        and download tools take as input.
        """)]
    public static Task<PagedResult<DocumentSummary>> ListDocumentsAsync(
        DocumentQueryService documents,
        ToolPaging paging,
        [Description(
            "Case-insensitive match against the document display name.")]
        string? nameContains = null,
        [Description(
            "Case-insensitive match against the original Windows path, for " +
            @"example C:\Users\accounts\Invoices.")]
        string? pathContains = null,
        [Description("Restrict to one device code, for example EPD-014.")]
        string? deviceCode = null,
        [Description("Restrict to one department.")]
        string? department = null,
        [Description("Restrict to one classification label.")]
        string? classification = null,
        [Description(
            "Restrict to one ingestion source: automatic_upload or " +
            "old_user_data.")]
        string? sourceType = null,
        [Description("Restrict to one file extension, for example .xlsx")]
        string? extension = null,
        [Description("Only files at least this many bytes.")]
        long? minSizeBytes = null,
        [Description("Only files at most this many bytes.")]
        long? maxSizeBytes = null,
        [Description("Only files modified at or after this UTC timestamp.")]
        DateTime? modifiedAfterUtc = null,
        [Description("Only files modified at or before this UTC timestamp.")]
        DateTime? modifiedBeforeUtc = null,
        [Description(
            "Include documents marked deleted. Defaults to false.")]
        bool includeDeleted = false,
        [Description(
            "Sort field: updated (default), created, name, or size.")]
        string sortBy = "updated",
        [Description("Sort largest or newest first. Defaults to true.")]
        bool descending = true,
        [Description("Number of documents to skip, for paging.")]
        int offset = 0,
        [Description("Maximum documents to return. Defaults to 20, max 100.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var filter = DocumentFilter.FromToolArguments(
            deviceCode,
            department,
            classification,
            sourceType,
            extension,
            nameContains,
            pathContains,
            extractionStatus: null,
            modifiedAfterUtc,
            modifiedBeforeUtc,
            minSizeBytes,
            maxSizeBytes,
            includeDeleted
        );

        return documents.ListDocumentsAsync(
            filter,
            NormalizeSort(sortBy),
            descending,
            paging.NormalizeOffset(offset),
            paging.NormalizeLimit(limit),
            cancellationToken
        );
    }

    private static string NormalizeSort(string sortBy)
    {
        var normalized = (sortBy ?? "").Trim().ToLowerInvariant();

        return normalized switch
        {
            "created" or "name" or "size" or "updated" => normalized,
            "" => "updated",
            _ => throw new McpToolException(
                $"Unknown sortBy '{sortBy}'. Use updated, created, name, or size."
            )
        };
    }
}
