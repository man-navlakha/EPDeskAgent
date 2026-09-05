using System.ComponentModel;
using EPDeskMcpServer.Contracts;
using EPDeskMcpServer.Services;
using ModelContextProtocol.Server;

namespace EPDeskMcpServer.Tools;

/// <summary>
/// Getting at the stored bytes: deep metadata, temporary download links, and
/// bounded inline reads.
/// </summary>
[McpServerToolType]
public sealed class FileAccessTools
{
    [McpServerTool(
        Name = "epdesk_get_file_metadata",
        Title = "Get full file metadata",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Everything recorded about one stored file: exact byte size, SHA-256,
        declared and detected content types, object storage location and
        version, source device and path, and the list of derivatives stored
        against it.

        By default this also checks object storage live and reports whether the
        bytes are really there and whether their size matches the database,
        which is how you tell a metadata problem from actual data loss. Set
        verifyStorage to false to skip that check and answer faster.
        """)]
    public static Task<FileMetadata> GetFileMetadataAsync(
        FileAccessService files,
        [Description(
            "Inspect the newest version of this document. Give this or " +
            "versionId.")]
        Guid? documentId = null,
        [Description("Inspect this exact version.")]
        Guid? versionId = null,
        [Description(
            "Check object storage live for existence and size. Defaults to true.")]
        bool verifyStorage = true,
        CancellationToken cancellationToken = default)
    {
        return files.GetFileMetadataAsync(
            documentId,
            versionId,
            verifyStorage,
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_get_download_url",
        Title = "Create a temporary download link",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Create a time-limited private download URL for the original file, or
        for one of its derivatives. This is the right way to hand a real file to
        a person or to another system: the link works without credentials and
        expires on its own.

        Give documentId for the newest version, versionId for an exact
        revision, or derivativeId for a generated artefact listed by
        epdesk_get_file_metadata.

        The link is a bearer credential for that one object. Treat it as
        sensitive, and prefer a short expiry when sharing.
        """)]
    public static Task<DownloadTicket> GetDownloadUrlAsync(
        FileAccessService files,
        [Description(
            "Link the newest version of this document. Give one of the three " +
            "id arguments.")]
        Guid? documentId = null,
        [Description("Link this exact version.")]
        Guid? versionId = null,
        [Description(
            "Link a derivative instead of the original file, using an id from " +
            "epdesk_get_file_metadata.")]
        Guid? derivativeId = null,
        [Description(
            "How long the link stays valid, in minutes. Defaults to 15, " +
            "maximum 720.")]
        int? expiresInMinutes = null,
        CancellationToken cancellationToken = default)
    {
        return files.CreateDownloadUrlAsync(
            documentId,
            versionId,
            derivativeId,
            expiresInMinutes,
            cancellationToken
        );
    }

    [McpServerTool(
        Name = "epdesk_read_file_content",
        Title = "Read raw file bytes inline",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        """
        Read the raw stored file directly: CSVs you want to parse, JSON or XML
        config, logs, or any other text-like file you need the contents of.

        Text-like files come back decoded as UTF-8. Anything else comes back
        base64-encoded and only if it is small; larger binaries are refused with
        a pointer to epdesk_get_download_url, because pushing megabytes of
        binary through this connection helps nobody.

        For ordinary documents such as PDFs and Office files, prefer
        epdesk_get_download_url and open it outside this connection.
        """)]
    public static Task<InlineFileContent> ReadFileContentAsync(
        FileAccessService files,
        [Description(
            "Read the newest version of this document. Give one of the three " +
            "id arguments.")]
        Guid? documentId = null,
        [Description("Read this exact version.")]
        Guid? versionId = null,
        [Description("Read a derivative instead of the original file.")]
        Guid? derivativeId = null,
        [Description(
            "Stop after this many bytes. Defaults to and is capped by the " +
            "server inline limit of 1 MB.")]
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        return files.ReadFileAsync(
            documentId,
            versionId,
            derivativeId,
            maxBytes,
            cancellationToken
        );
    }
}
