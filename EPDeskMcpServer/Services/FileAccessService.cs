using System.Text;
using EPDeskMcpServer.Configuration;
using EPDeskMcpServer.Contracts;
using EPDeskServerApi.Data;
using EPDeskServerApi.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskMcpServer.Services;

/// <summary>
/// Everything that touches the bytes in Backblaze: deep metadata, presigned
/// download links, and bounded inline reads.
/// </summary>
public sealed class FileAccessService
{
    private const string VersionTarget = "version";
    private const string DerivativeTarget = "derivative";

    private static readonly HashSet<string> TextExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".yaml",
        ".yml", ".html", ".htm", ".ini", ".cfg", ".conf", ".sql", ".srt"
    };

    private static readonly HashSet<string> TextMediaTypes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/csv",
        "application/x-ndjson", "application/yaml", "application/x-yaml",
        "application/javascript", "application/sql"
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IObjectStorageService _storage;
    private readonly EpDeskMcpOptions _options;

    public FileAccessService(
        IDbContextFactory<AppDbContext> dbFactory,
        IObjectStorageService storage,
        IOptions<EpDeskMcpOptions> options)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _options = options.Value;
    }

    public async Task<FileMetadata> GetFileMetadataAsync(
        Guid? documentId,
        Guid? versionId,
        bool verifyStorage,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var version = await DocumentQueryService.ResolveVersionAsync(
            db,
            documentId,
            versionId,
            cancellationToken
        );

        var derivatives = await db.DocumentDerivatives
            .AsNoTracking()
            .Where(x => x.DocumentVersionId == version.Id)
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Ordinal)
            .Select(x => new DerivativeSummary(
                x.Id,
                x.Kind,
                x.Ordinal,
                x.ContentType,
                x.SizeBytes,
                x.ObjectKey,
                x.PipelineVersion
            ))
            .ToListAsync(cancellationToken);

        var storage = verifyStorage
            ? await DescribeStorageAsync(
                version.ObjectKey,
                version.SizeBytes,
                cancellationToken)
            : new StoredObjectStatus(
                false, null, null, null, null, null, false);

        var paths = await SourcePathResolver.ResolveAsync(
            db,
            [(version.SourceType, version.SourceRecordId)],
            cancellationToken
        );

        return new FileMetadata(
            version.DocumentId,
            version.Id,
            version.VersionNumber,
            version.FileName,
            version.FileExtension,
            SourcePathResolver.Lookup(paths, version.SourceRecordId),
            version.SourceType,
            version.DeviceCode,
            version.Department,
            version.Classification,
            version.SizeBytes,
            version.DeclaredContentType,
            version.DetectedContentType,
            version.Sha256,
            version.BucketName,
            version.ObjectKey,
            version.B2VersionId,
            version.ObjectETag,
            version.SourceModifiedAtUtc,
            version.ExtractionStatus,
            version.ExtractionPipelineVersion,
            version.SectionCount,
            version.PageCount,
            version.ExtractionMetadataJson,
            version.ExtractionErrorCode,
            version.ExtractionError,
            version.ExtractedAtUtc,
            storage,
            derivatives
        );
    }

    public async Task<DownloadTicket> CreateDownloadUrlAsync(
        Guid? documentId,
        Guid? versionId,
        Guid? derivativeId,
        int? expiresInMinutes,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var target = await ResolveTargetAsync(
            db,
            documentId,
            versionId,
            derivativeId,
            cancellationToken
        );

        var minutes = Math.Clamp(
            expiresInMinutes ?? _options.DownloadUrlMinutes,
            1,
            720
        );

        var validFor = TimeSpan.FromMinutes(minutes);

        if (!await _storage.ObjectExistsAsync(target.ObjectKey, cancellationToken))
        {
            throw new McpToolException(
                $"The stored object for '{target.FileName}' is missing from " +
                $"bucket key '{target.ObjectKey}'. The database row exists but " +
                "the bytes do not, so this file needs re-uploading."
            );
        }

        var url = await _storage.CreateDownloadUrlAsync(
            target.ObjectKey,
            validFor,
            cancellationToken
        );

        return new DownloadTicket(
            target.Kind,
            target.Id,
            target.FileName,
            target.ObjectKey,
            target.SizeBytes,
            target.ContentType,
            target.Sha256,
            url.ToString(),
            DateTime.UtcNow.Add(validFor)
        );
    }

    public async Task<InlineFileContent> ReadFileAsync(
        Guid? documentId,
        Guid? versionId,
        Guid? derivativeId,
        long? maxBytes,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var target = await ResolveTargetAsync(
            db,
            documentId,
            versionId,
            derivativeId,
            cancellationToken
        );

        var limit = Math.Clamp(
            maxBytes ?? _options.MaxInlineDownloadBytes,
            1,
            _options.MaxInlineDownloadBytes
        );

        var isText = LooksLikeText(target.FileName, target.ContentType);

        if (!isText && target.SizeBytes > limit)
        {
            throw new McpToolException(
                $"'{target.FileName}' is a {SnippetBuilder.DescribeBytes(target.SizeBytes)} " +
                $"binary file, over the {SnippetBuilder.DescribeBytes(limit)} inline limit. " +
                "Use epdesk_get_download_url to get a temporary link and " +
                "fetch the real file instead."
            );
        }

        using var read = await _storage.OpenReadAsync(
            target.ObjectKey,
            limit,
            cancellationToken
        );

        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer, cancellationToken);

        var bytes = buffer.ToArray();
        var truncated = target.SizeBytes > bytes.LongLength;

        if (isText)
        {
            var text = DecodeText(bytes);

            if (text.Length > _options.MaxTextCharacters)
            {
                text = text[.._options.MaxTextCharacters];
                truncated = true;
            }

            return new InlineFileContent(
                target.Kind,
                target.Id,
                target.FileName,
                target.ContentType,
                target.SizeBytes,
                bytes.LongLength,
                truncated,
                "utf-8",
                text
            );
        }

        return new InlineFileContent(
            target.Kind,
            target.Id,
            target.FileName,
            target.ContentType,
            target.SizeBytes,
            bytes.LongLength,
            truncated,
            "base64",
            Convert.ToBase64String(bytes)
        );
    }

    private async Task<StoredObjectStatus> DescribeStorageAsync(
        string objectKey,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            return new StoredObjectStatus(
                false, null, null, null, null, null, false);
        }

        var metadata = await _storage.GetObjectMetadataAsync(
            objectKey,
            cancellationToken
        );

        if (metadata is null)
        {
            return new StoredObjectStatus(
                false, null, null, null, null, null, false);
        }

        return new StoredObjectStatus(
            true,
            metadata.SizeBytes,
            metadata.ContentType,
            metadata.ETag,
            metadata.VersionId,
            metadata.LastModifiedUtc,
            metadata.SizeBytes == expectedSizeBytes
        );
    }

    private static async Task<StorageTarget> ResolveTargetAsync(
        AppDbContext db,
        Guid? documentId,
        Guid? versionId,
        Guid? derivativeId,
        CancellationToken cancellationToken)
    {
        if (derivativeId is not null)
        {
            var derivative = await db.DocumentDerivatives
                .AsNoTracking()
                .Where(x => x.Id == derivativeId)
                .Select(x => new
                {
                    x.Id,
                    x.Kind,
                    x.Ordinal,
                    x.ObjectKey,
                    x.ContentType,
                    x.SizeBytes,
                    x.Sha256,
                    x.DocumentVersion.FileName
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (derivative is null)
            {
                throw new McpToolException(
                    $"No derivative found with id {derivativeId}. " +
                    "epdesk_get_file_metadata lists the derivatives available " +
                    "for a document version."
                );
            }

            return new StorageTarget(
                DerivativeTarget,
                derivative.Id,
                BuildDerivativeFileName(
                    derivative.FileName,
                    derivative.Kind,
                    derivative.Ordinal,
                    derivative.ContentType
                ),
                derivative.ObjectKey,
                derivative.SizeBytes,
                derivative.ContentType,
                derivative.Sha256
            );
        }

        var version = await DocumentQueryService.ResolveVersionAsync(
            db,
            documentId,
            versionId,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(version.ObjectKey))
        {
            throw new McpToolException(
                $"Version {version.Id} of '{version.FileName}' has no stored " +
                "object key, so there is nothing to download."
            );
        }

        var contentType = string.IsNullOrWhiteSpace(version.DetectedContentType)
            ? version.DeclaredContentType
            : version.DetectedContentType;

        return new StorageTarget(
            VersionTarget,
            version.Id,
            version.FileName,
            version.ObjectKey,
            version.SizeBytes,
            contentType,
            version.Sha256
        );
    }

    /// <summary>
    /// Media types are matched exactly rather than by substring. A .pptx is
    /// announced as application/vnd.openxmlformats-officedocument..., so a
    /// "contains xml" test would decode a zip archive as text and hand a model
    /// a page of mojibake.
    /// </summary>
    /// <summary>
    /// A derivative has no filename of its own, only a kind and an ordinal.
    /// Naming it after its source with a real extension means a download link
    /// saves as something openable rather than as an extensionless blob.
    /// </summary>
    private static string BuildDerivativeFileName(
        string sourceFileName,
        string kind,
        int ordinal,
        string contentType)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceFileName);
        var suffix = ordinal == 0 ? kind : $"{kind}-{ordinal}";

        var extension = contentType.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "application/json" => ".json",
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "text/csv" => ".csv",
            "text/plain" => ".txt",
            "application/vnd.apache.parquet" or
                "application/x-parquet" => ".parquet",
            _ => ".bin"
        };

        return $"{stem}.{suffix}{extension}";
    }

    private static bool LooksLikeText(string fileName, string contentType)
    {
        if (TextExtensions.Contains(Path.GetExtension(fileName)))
        {
            return true;
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mediaType = contentType.Split(';')[0].Trim();

        return TextMediaTypes.Contains(mediaType) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Honours a UTF-8 or UTF-16 byte-order mark when present, since Windows
    /// tooling writes both, and falls back to UTF-8.
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true
        );

        return reader.ReadToEnd();
    }

    private sealed record StorageTarget(
        string Kind,
        Guid Id,
        string FileName,
        string ObjectKey,
        long SizeBytes,
        string ContentType,
        string Sha256
    );
}
