using System.ComponentModel;
using EPDeskMcpServer.Contracts;
using EPDeskMcpServer.Services;
using ModelContextProtocol.Server;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// The text-extraction surface: full-text search, extracted-text reads, queue
/// health, and the guarded requeue action.
///
/// <para>
/// <b>These tools are deliberately not exposed.</b> Program.cs registers tool
/// types explicitly and omits this one, so none of it reaches tools/list. The
/// extraction pipeline was shelved while the corpus is served as metadata plus
/// downloads, and this class is kept intact so the surface can come back
/// without being rewritten.
/// </para>
///
/// <para>
/// To re-enable: add <c>.WithTools&lt;ExtractionTools&gt;()</c> to the MCP
/// builder chain in Program.cs, restore the extraction paragraphs in
/// ServerInstructions, and start the epdesk-extraction-worker Railway service
/// so the queue drains. epdesk_requeue_extraction additionally needs
/// Mcp__EnableWriteTools=true.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ExtractionTools
{
    [McpServerTool(
        Name = "epdesk_search_documents",
        Title = "Search document contents",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Full-text search across the text extracted from every uploaded document
        (PDF pages, Office files, spreadsheets, slides, OCR output). This is the
        primary way to answer "which of our files mentions X".

        Supports quoted phrases, OR, and leading - to exclude, for example:
        "purchase order" invoice -draft

        Returns one hit per document by default, ranked by relevance, each with
        a short snippet and the ids needed by the other tools. Set
        groupByDocument to false to see every matching page or sheet separately.

        Searching only matches documents whose extraction completed. To find a
        file by its name or Windows path instead, use epdesk_list_documents with
        nameContains or pathContains.
        """)]
    public static Task<SearchResult> SearchDocumentsAsync(
        DocumentQueryService documents,
        ToolPaging paging,
        [Description("Words or phrases to look for inside document text.")]
        string query,
        [Description(
            "Return the single best-matching page per document. Set to false " +
            "to see every matching section. Defaults to true.")]
        bool groupByDocument = true,
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
        [Description("Restrict to one file extension, for example .pdf")]
        string? extension = null,
        [Description(
            "Only files modified at or after this UTC timestamp, " +
            "for example 2026-01-01T00:00:00Z.")]
        DateTime? modifiedAfterUtc = null,
        [Description("Only files modified at or before this UTC timestamp.")]
        DateTime? modifiedBeforeUtc = null,
        [Description("Number of hits to skip, for paging. Defaults to 0.")]
        int offset = 0,
        [Description("Maximum hits to return. Defaults to 20, maximum 100.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var filter = DocumentFilter.FromToolArguments(
            deviceCode,
            department,
            classification,
            sourceType,
            extension,
            nameContains: null,
            pathContains: null,
            extractionStatus: null,
            modifiedAfterUtc,
            modifiedBeforeUtc,
            minSizeBytes: null,
            maxSizeBytes: null,
            includeDeleted: false
        );

        return documents.SearchAsync(
            query,
            filter,
            paging.NormalizeOffset(offset),
            paging.NormalizeLimit(limit),
            groupByDocument,
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_read_document",
        Title = "Read extracted document text",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Read the text extracted from a document, in order, as sections. A
        section is one page, slide, heading, or spreadsheet sheet, so each piece
        of text stays citable back to its position in the file.

        Give either documentId (reads the newest version) or versionId (reads
        that exact revision). Text is capped per call; when the response has
        truncated set to true, call again with offset set to nextOffset to
        continue.

        This returns extracted text, not the original bytes. For the real file
        use epdesk_get_download_url, and for small text files
        epdesk_read_file_content.
        """)]
    public static Task<DocumentContent> ReadDocumentAsync(
        DocumentQueryService documents,
        ToolPaging paging,
        [Description(
            "Read the newest version of this document. Give this or versionId.")]
        Guid? documentId = null,
        [Description(
            "Read this exact version. Takes precedence over documentId.")]
        Guid? versionId = null,
        [Description(
            "Section index to start at, for continuing a long document. " +
            "Defaults to 0.")]
        int offset = 0,
        [Description(
            "Maximum characters of text to return in this call. Defaults to " +
            "the server limit of 40000.")]
        int? maxCharacters = null,
        CancellationToken cancellationToken = default)
    {
        return documents.ReadContentAsync(
            documentId,
            versionId,
            paging.NormalizeOffset(offset),
            paging.NormalizeCharacters(maxCharacters),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_get_extraction_status",
        Title = "Inspect the extraction queue",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Health of the text-extraction pipeline: job counts by state, document
        version counts by extraction state, and the most recent failures with
        their error codes, attempt counts, and retry times.

        Read this when search returns less than expected, since a document whose
        extraction never completed has no searchable text. Failing versions
        listed here can be retried with epdesk_requeue_extraction when write
        tools are enabled.
        """)]
    public static Task<ExtractionOverview> GetExtractionStatusAsync(
        InsightsService insights,
        [Description(
            "How many recent failures to include. Defaults to 20, max 100.")]
        int failureLimit = 20,
        CancellationToken cancellationToken = default)
    {
        return insights.GetExtractionOverviewAsync(
            Math.Clamp(failureLimit, 1, 100),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_requeue_extraction",
        Title = "Retry a failed extraction",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Put a document version back on the extraction queue after a failure, so
        the worker tries again on its next poll. Nothing is deleted or
        overwritten: this only resets the job's status, attempt count, and lease.

        Only useful for versions whose extraction failed, was rejected, or is
        parked in retry_wait or dead_letter, all of which
        epdesk_get_extraction_status lists. A job that is currently running is
        refused rather than interrupted.

        Disabled unless the server is configured with write tools enabled.
        """)]
    public static Task<RequeueResult> RequeueExtractionAsync(
        ExtractionMaintenanceService maintenance,
        [Description(
            "The versionId to retry, from epdesk_get_extraction_status or " +
            "epdesk_get_document.")]
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        return maintenance.RequeueAsync(versionId, cancellationToken);
    }
}
