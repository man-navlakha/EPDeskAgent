using System.ComponentModel;
using EPDeskMcpServer.Contracts;
using EPDeskMcpServer.Services;
using ModelContextProtocol.Server;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// Operational visibility over the device fleet, the ingestion pipelines, and
/// the shape of the stored corpus.
/// </summary>
[McpServerToolType]
public sealed class OperationsTools
{
    [McpServerTool(
        Name = "epdesk_list_devices",
        Title = "List agent devices",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        List the Windows machines running the EPDesk agent, newest contact
        first, with how many documents and how many bytes each has contributed.

        Use this to learn the valid deviceCode values before filtering any other
        tool by device, and to spot machines that have stopped reporting.
        """)]
    public static Task<PagedResult<DeviceSummary>> ListDevicesAsync(
        InsightsService insights,
        ToolPaging paging,
        [Description("Restrict to one status, for example online or offline.")]
        string? status = null,
        [Description(
            "Include devices that were deactivated. Defaults to false.")]
        bool includeInactive = false,
        [Description("Number of devices to skip, for paging.")]
        int offset = 0,
        [Description("Maximum devices to return. Defaults to 20, max 100.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return insights.ListDevicesAsync(
            status,
            includeInactive,
            paging.NormalizeOffset(offset),
            paging.NormalizeLimit(limit),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_list_uploads",
        Title = "List file ingestion records",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        List raw ingestion rows from both pipelines: files the agent uploaded
        automatically, and files pulled in from the old user-data archive. Each
        row carries its original Windows path, upload status, any error, and the
        documentId it became once it reached the catalogue.

        This is the tool for "did this file ever arrive" and "what is stuck".
        A row with no documentId never made it into the catalogue, so it will
        not appear in epdesk_list_documents and cannot be downloaded.
        """)]
    public static Task<PagedResult<IngestionRecord>> ListUploadsAsync(
        InsightsService insights,
        ToolPaging paging,
        [Description(
            "Restrict to automatic_upload or old_user_data. Omit for both.")]
        string? sourceType = null,
        [Description("Restrict to one device code.")]
        string? deviceCode = null,
        [Description(
            "Restrict to one status, for example uploading, completed, " +
            "failed, skipped, or missing.")]
        string? status = null,
        [Description("Number of rows to skip, for paging.")]
        int offset = 0,
        [Description("Maximum rows to return. Defaults to 20, max 100.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return insights.ListIngestionAsync(
            sourceType,
            deviceCode,
            status,
            paging.NormalizeOffset(offset),
            paging.NormalizeLimit(limit),
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_get_storage_stats",
        Title = "Summarise the document corpus",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Aggregate view of the corpus: document and version counts, total bytes,
        and breakdowns by file type, device, department, and ingestion source
        (automatic_upload from the agent, old_user_data from the archive import).

        Accepts the same filters as epdesk_list_documents, so you can scope the
        summary to one device or department. Start here when the question is
        about volume, coverage, or composition rather than about a specific file.
        """)]
    public static Task<StorageStats> GetStorageStatsAsync(
        InsightsService insights,
        [Description("Restrict to one device code.")]
        string? deviceCode = null,
        [Description("Restrict to one department.")]
        string? department = null,
        [Description("Restrict to one classification label.")]
        string? classification = null,
        [Description(
            "Restrict to automatic_upload or old_user_data.")]
        string? sourceType = null,
        [Description("Restrict to one file extension, for example .pdf")]
        string? extension = null,
        [Description(
            "Include documents marked deleted. Defaults to false.")]
        bool includeDeleted = false,
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
            modifiedAfterUtc: null,
            modifiedBeforeUtc: null,
            minSizeBytes: null,
            maxSizeBytes: null,
            includeDeleted
        );

        return insights.GetStorageStatsAsync(filter, cancellationToken);
    }
}
