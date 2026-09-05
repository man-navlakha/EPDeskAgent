using EPDeskServerApi.Data;
using EPDeskServerApi.Models;
using EPDeskServerApi.Services;
using Microsoft.EntityFrameworkCore;

namespace EPDeskMcpServer.Services;

/// <summary>
/// The filter set shared by search and browse. Keeping it in one place means a
/// model that learns the filters for one tool already knows them for the other.
/// </summary>
public sealed record DocumentFilter
{
    public string? DeviceCode { get; init; }
    public string? Department { get; init; }
    public string? Classification { get; init; }
    public string? SourceType { get; init; }
    public string? Extension { get; init; }
    public string? NameContains { get; init; }
    public string? PathContains { get; init; }
    public string? ExtractionStatus { get; init; }
    public DateTime? ModifiedAfterUtc { get; init; }
    public DateTime? ModifiedBeforeUtc { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public bool IncludeDeleted { get; init; }

    public static DocumentFilter FromToolArguments(
        string? deviceCode,
        string? department,
        string? classification,
        string? sourceType,
        string? extension,
        string? nameContains,
        string? pathContains,
        string? extractionStatus,
        DateTime? modifiedAfterUtc,
        DateTime? modifiedBeforeUtc,
        long? minSizeBytes,
        long? maxSizeBytes,
        bool includeDeleted)
    {
        return new DocumentFilter
        {
            DeviceCode = Normalize(deviceCode)?.ToUpperInvariant(),
            Department = Normalize(department),
            Classification = Normalize(classification)?.ToLowerInvariant(),
            SourceType = Normalize(sourceType)?.ToLowerInvariant(),
            Extension = NormalizeExtension(extension),
            NameContains = Normalize(nameContains),
            PathContains = Normalize(pathContains),
            ExtractionStatus = Normalize(extractionStatus)?.ToLowerInvariant(),
            ModifiedAfterUtc = AsUtc(modifiedAfterUtc),
            ModifiedBeforeUtc = AsUtc(modifiedBeforeUtc),
            MinSizeBytes = minSizeBytes,
            MaxSizeBytes = maxSizeBytes,
            IncludeDeleted = includeDeleted
        };
    }

    public void Validate()
    {
        if (SourceType is not null &&
            SourceType != DocumentExtractionQueueService.AutomaticUploadSourceType &&
            SourceType != DocumentExtractionQueueService.OldUserDataSourceType)
        {
            throw new McpToolException(
                $"Unknown sourceType '{SourceType}'. Use " +
                $"'{DocumentExtractionQueueService.AutomaticUploadSourceType}' or " +
                $"'{DocumentExtractionQueueService.OldUserDataSourceType}', or omit it."
            );
        }

        if (MinSizeBytes is < 0 || MaxSizeBytes is < 0)
        {
            throw new McpToolException("Size filters cannot be negative.");
        }

        if (MinSizeBytes.HasValue &&
            MaxSizeBytes.HasValue &&
            MinSizeBytes > MaxSizeBytes)
        {
            throw new McpToolException(
                "minSizeBytes must be less than or equal to maxSizeBytes."
            );
        }

        if (ModifiedAfterUtc.HasValue &&
            ModifiedBeforeUtc.HasValue &&
            ModifiedAfterUtc > ModifiedBeforeUtc)
        {
            throw new McpToolException(
                "modifiedAfterUtc must be earlier than modifiedBeforeUtc."
            );
        }
    }

    /// <summary>
    /// Applies document-level predicates. Version-level predicates use "has any
    /// version matching" semantics, which is what a person means when they ask
    /// for "the PDFs from this device".
    /// </summary>
    public IQueryable<Document> Apply(
        IQueryable<Document> documents,
        AppDbContext db)
    {
        if (!IncludeDeleted)
        {
            documents = documents.Where(d => !d.IsDeleted);
        }

        if (DeviceCode is not null)
        {
            documents = documents.Where(d => d.DeviceCode == DeviceCode);
        }

        if (Department is not null)
        {
            documents = documents.Where(d => d.Department == Department);
        }

        if (Classification is not null)
        {
            documents = documents.Where(d => d.Classification == Classification);
        }

        if (SourceType is not null)
        {
            documents = documents.Where(d => d.SourceType == SourceType);
        }

        if (NameContains is not null)
        {
            documents = documents.Where(d =>
                EF.Functions.ILike(d.DisplayName, $"%{EscapeLike(NameContains)}%")
            );
        }

        if (PathContains is not null)
        {
            var pattern = $"%{EscapeLike(PathContains)}%";

            var automaticIds = db.AutomaticFileUploads
                .Where(u => EF.Functions.ILike(u.FullPath, pattern))
                .Select(u => u.Id);

            var oldUserDataIds = db.OldUserDataFiles
                .Where(f => EF.Functions.ILike(f.FullPath, pattern))
                .Select(f => f.Id);

            documents = documents.Where(d =>
                automaticIds.Contains(d.SourceRecordId) ||
                oldUserDataIds.Contains(d.SourceRecordId)
            );
        }

        if (Extension is not null)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v => v.FileExtension.ToLower() == Extension)
            );
        }

        if (ExtractionStatus is not null)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v => v.ExtractionStatus == ExtractionStatus)
            );
        }

        if (MinSizeBytes.HasValue)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v => v.SizeBytes >= MinSizeBytes.Value)
            );
        }

        if (MaxSizeBytes.HasValue)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v => v.SizeBytes <= MaxSizeBytes.Value)
            );
        }

        if (ModifiedAfterUtc.HasValue)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v =>
                    v.SourceModifiedAtUtc != null &&
                    v.SourceModifiedAtUtc >= ModifiedAfterUtc.Value
                )
            );
        }

        if (ModifiedBeforeUtc.HasValue)
        {
            documents = documents.Where(d =>
                d.Versions.Any(v =>
                    v.SourceModifiedAtUtc != null &&
                    v.SourceModifiedAtUtc <= ModifiedBeforeUtc.Value
                )
            );
        }

        return documents;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeExtension(string? value)
    {
        var normalized = Normalize(value)?.ToLowerInvariant();

        if (normalized is null)
        {
            return null;
        }

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    /// <summary>
    /// Npgsql rejects timestamptz parameters that are not tagged UTC, and a
    /// model has no way to express a kind in JSON, so unspecified means UTC.
    /// </summary>
    private static DateTime? AsUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }
}
