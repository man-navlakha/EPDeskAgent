using System.ComponentModel;
using EPDeskMcpServer.Contracts;
using EPDeskMcpServer.Services;
using ModelContextProtocol.Server;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// Opening a document once it has been found: its full record and every
/// stored version.
/// </summary>
[McpServerToolType]
public sealed class DocumentReadTools
{
    [McpServerTool(
        Name = "epdesk_get_document",
        Title = "Get document detail and versions",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Full record for one document: where it came from, its original Windows
        path, and every stored version with size, content type, and the
        derivative kinds stored against it.

        A document is one logical file. Each time the source file changed, a new
        immutable version was stored, so versions are ordered newest first. Use
        a versionId from here when you need a specific revision rather than the
        current one.
        """)]
    public static Task<DocumentDetail> GetDocumentAsync(
        DocumentQueryService documents,
        [Description("The documentId returned by epdesk_list_documents.")]
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return documents.GetDocumentAsync(documentId, cancellationToken);
    }
}
