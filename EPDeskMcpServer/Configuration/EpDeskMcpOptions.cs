namespace EPDeskMcpServer.Configuration;

/// <summary>
/// Behaviour and safety limits for the MCP surface. Every limit exists to keep
/// a single tool call from flooding a model's context window.
/// </summary>
/// <remarks>
/// This server carries no authentication: the MCP endpoint is open to anyone
/// who reaches it. That is a deliberate deployment choice, so treat the URL
/// itself as the only thing standing between the public internet and the
/// document corpus.
/// </remarks>
public sealed class EpDeskMcpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// Enables the small set of tools that mutate state, currently limited to
    /// re-queueing a failed extraction.
    /// </summary>
    public bool EnableWriteTools { get; set; }

    public int DefaultPageSize { get; set; } = 20;

    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// Ceiling on extracted text returned by one document read.
    /// </summary>
    public int MaxTextCharacters { get; set; } = 40_000;

    /// <summary>
    /// Ceiling on raw bytes a client may pull through the MCP connection.
    /// Anything larger has to go through a presigned download URL so the bytes
    /// never pass through a model's context.
    /// </summary>
    public long MaxInlineDownloadBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Lifetime of presigned download URLs handed to MCP clients.
    /// </summary>
    public int DownloadUrlMinutes { get; set; } = 15;
}
