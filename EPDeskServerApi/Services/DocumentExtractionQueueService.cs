using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EPDeskServerApi.Configuration;
using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Services;

/// <summary>
/// Creates canonical document, immutable version, and extraction-job records.
/// Callers must own a database transaction and save/commit after this service
/// returns so upload completion and queue creation remain atomic.
/// </summary>
public sealed class DocumentExtractionQueueService
{
    public const string AutomaticUploadSourceType = "automatic_upload";
    public const string OldUserDataSourceType = "old_user_data";
    public const string CurrentPipelineVersion = "v1";

    private readonly AppDbContext _db;
    private readonly B2StorageOptions _storageOptions;

    public DocumentExtractionQueueService(
        AppDbContext db,
        IOptions<B2StorageOptions> storageOptions)
    {
        _db = db;
        _storageOptions = storageOptions.Value;
    }

    public Task<DocumentQueueResult> EnsureAutomaticUploadQueuedAsync(
        AutomaticFileUpload upload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(upload.Status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Automatic upload {upload.Id} must be completed before extraction is queued."
            );
        }

        return EnsureQueuedAsync(
            new SourceSnapshot(
                AutomaticUploadSourceType,
                upload.Id,
                upload.DeviceCode,
                upload.FileName,
                upload.Extension,
                upload.ObjectKey,
                upload.B2VersionId,
                upload.ObjectETag,
                NormalizeSha256(upload.Sha256),
                upload.SizeBytes,
                upload.LastModifiedAtUtc,
                upload.ContentType
            ),
            cancellationToken
        );
    }

    public Task<DocumentQueueResult> EnsureOldUserDataFileQueuedAsync(
        OldUserDataFile file,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(file.Status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Old User Data file {file.Id} must be completed before extraction is queued."
            );
        }

        return EnsureQueuedAsync(
            new SourceSnapshot(
                OldUserDataSourceType,
                file.Id,
                file.DeviceCode,
                file.FileName,
                file.Extension,
                file.ObjectKey,
                file.B2VersionId,
                file.ObjectETag,
                NormalizeSha256(file.Sha256),
                file.SizeBytes,
                file.UpdatedAtUtc,
                file.ContentType
            ),
            cancellationToken
        );
    }

    private async Task<DocumentQueueResult> EnsureQueuedAsync(
        SourceSnapshot source,
        CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Document extraction queue creation requires an explicit database transaction."
            );
        }

        ValidateSource(source);

        var lockIdentity = $"document-extraction:{source.SourceType}:{source.SourceRecordId:N}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))",
            cancellationToken
        );

        var now = DateTime.UtcNow;
        var document = _db.Documents.Local.FirstOrDefault(
            x => x.SourceType == source.SourceType &&
                 x.SourceRecordId == source.SourceRecordId
        );

        document ??= await _db.Documents.FirstOrDefaultAsync(
            x => x.SourceType == source.SourceType &&
                 x.SourceRecordId == source.SourceRecordId,
            cancellationToken
        );

        var documentCreated = document is null;

        if (document is null)
        {
            document = new Document
            {
                Id = Guid.NewGuid(),
                SourceType = source.SourceType,
                SourceRecordId = source.SourceRecordId,
                DeviceCode = source.DeviceCode,
                DisplayName = source.FileName,
                Classification = "unclassified",
                Department = "",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _db.Documents.Add(document);
        }
        else
        {
            document.DeviceCode = source.DeviceCode;
            document.DisplayName = source.FileName;
            document.UpdatedAtUtc = now;
        }

        var sourceVersionKey = CreateSourceVersionKey(source);
        var version = _db.DocumentVersions.Local.FirstOrDefault(
            x => x.DocumentId == document.Id &&
                 x.SourceVersionKey == sourceVersionKey
        );

        version ??= await _db.DocumentVersions.FirstOrDefaultAsync(
            x => x.DocumentId == document.Id &&
                 x.SourceVersionKey == sourceVersionKey,
            cancellationToken
        );

        var versionCreated = version is null;

        if (version is null)
        {
            var latestVersionNumber = await _db.DocumentVersions
                .Where(x => x.DocumentId == document.Id)
                .Select(x => (int?)x.VersionNumber)
                .MaxAsync(cancellationToken) ?? 0;

            version = new DocumentVersion
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                VersionNumber = latestVersionNumber + 1,
                SourceVersionKey = sourceVersionKey,
                FileName = source.FileName,
                FileExtension = NormalizeExtension(source.Extension),
                BucketName = _storageOptions.BucketName,
                ObjectKey = source.ObjectKey,
                B2VersionId = source.B2VersionId,
                ObjectETag = source.ObjectETag,
                Sha256 = source.Sha256,
                SizeBytes = source.SizeBytes,
                SourceModifiedAtUtc = ForceUtc(source.SourceModifiedAtUtc),
                DeclaredContentType = string.IsNullOrWhiteSpace(source.ContentType)
                    ? "application/octet-stream"
                    : source.ContentType.Trim(),
                ExtractionStatus = "queued",
                ExtractionPipelineVersion = "",
                ExtractionMetadataJson = "{}",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            // The version ID is part of every deterministic derivative key.
            version.DerivativePrefix = CreateDerivativePrefix(document.Id, version.Id);
            _db.DocumentVersions.Add(version);
        }
        else if (!string.IsNullOrWhiteSpace(source.Sha256))
        {
            if (string.IsNullOrWhiteSpace(version.Sha256))
            {
                version.Sha256 = source.Sha256;
                version.UpdatedAtUtc = now;
            }
            else if (!string.Equals(
                         version.Sha256,
                         source.Sha256,
                         StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source checksum conflicts with document version {version.Id}."
                );
            }
        }

        var job = _db.ExtractionJobs.Local.FirstOrDefault(
            x => x.DocumentVersionId == version.Id &&
                 x.PipelineVersion == CurrentPipelineVersion
        );

        job ??= await _db.ExtractionJobs.FirstOrDefaultAsync(
            x => x.DocumentVersionId == version.Id &&
                 x.PipelineVersion == CurrentPipelineVersion,
            cancellationToken
        );

        var jobCreated = job is null;

        if (job is null)
        {
            var alreadyExtracted =
                string.Equals(version.ExtractionStatus, "completed", StringComparison.Ordinal) &&
                string.Equals(
                    version.ExtractionPipelineVersion,
                    CurrentPipelineVersion,
                    StringComparison.Ordinal
                );

            job = new ExtractionJob
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = version.Id,
                PipelineVersion = CurrentPipelineVersion,
                Status = alreadyExtracted ? "completed" : "queued",
                AttemptCount = 0,
                MaxAttempts = 5,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = alreadyExtracted ? now : null
            };

            if (!alreadyExtracted)
            {
                version.ExtractionStatus = "queued";
                version.ExtractionErrorCode = "";
                version.ExtractionError = "";
                version.UpdatedAtUtc = now;
            }

            _db.ExtractionJobs.Add(job);
        }

        return new DocumentQueueResult(
            document.Id,
            version.Id,
            job.Id,
            documentCreated,
            versionCreated,
            jobCreated
        );
    }

    private static string CreateDerivativePrefix(Guid documentId, Guid versionId)
    {
        return $"derivatives/{documentId:N}/{versionId:N}";
    }

    private static string CreateSourceVersionKey(SourceSnapshot source)
    {
        var parts = new[]
        {
            source.SourceType,
            source.SourceRecordId.ToString("N"),
            source.ObjectKey,
            source.B2VersionId,
            source.ObjectETag,
            source.SizeBytes.ToString(CultureInfo.InvariantCulture),
            ForceUtc(source.SourceModifiedAtUtc).Ticks.ToString(
                CultureInfo.InvariantCulture
            )
        };

        // Length-prefix every value so unusual object-key characters cannot
        // create an ambiguous canonical representation.
        var canonical = string.Concat(
            parts.Select(part =>
                $"{part.Length.ToString(CultureInfo.InvariantCulture)}:{part}"
            )
        );

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))
        ).ToLowerInvariant();
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return "";
        }

        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static string NormalizeSha256(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static DateTime ForceUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static void ValidateSource(SourceSnapshot source)
    {
        if (source.SourceRecordId == Guid.Empty)
        {
            throw new InvalidOperationException("A source record ID is required.");
        }

        if (string.IsNullOrWhiteSpace(source.FileName) ||
            string.IsNullOrWhiteSpace(source.ObjectKey))
        {
            throw new InvalidOperationException(
                $"Source {source.SourceType}/{source.SourceRecordId} has incomplete B2 metadata."
            );
        }

        if (source.SizeBytes <= 0)
        {
            throw new InvalidOperationException(
                $"Source {source.SourceType}/{source.SourceRecordId} is empty."
            );
        }

        if (!string.IsNullOrWhiteSpace(source.Sha256) &&
            (source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit)))
        {
            throw new InvalidOperationException(
                $"Source {source.SourceType}/{source.SourceRecordId} has an invalid SHA-256 checksum."
            );
        }
    }

    private sealed record SourceSnapshot(
        string SourceType,
        Guid SourceRecordId,
        string DeviceCode,
        string FileName,
        string Extension,
        string ObjectKey,
        string B2VersionId,
        string ObjectETag,
        string Sha256,
        long SizeBytes,
        DateTime SourceModifiedAtUtc,
        string ContentType
    );
}

public sealed record DocumentQueueResult(
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid ExtractionJobId,
    bool DocumentCreated,
    bool VersionCreated,
    bool JobCreated
);
