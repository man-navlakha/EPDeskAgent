using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EPDeskExtractionWorker.Services.Extraction;
using EPDeskExtractionWorker.Services.Jobs;

namespace EPDeskExtractionWorker.Services.Processing;

public sealed class ExtractionArtifactBuilder
{
    private const int MaximumSearchSectionCharacters = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public async Task<PreparedExtractionArtifact> PrepareAsync(
        ClaimedExtractionJob job,
        ExtractedDocument extracted,
        FileInspection inspection,
        DownloadedObject downloaded,
        string derivativeFilePath,
        CancellationToken cancellationToken)
    {
        var searchableSections = ExpandSearchSections(extracted.Sections);
        var sections = searchableSections
            .Select((section, ordinal) => MapSection(job, section, ordinal))
            .ToArray();

        var generatedAtUtc = DateTime.UtcNow;
        var envelope = new StructuredDerivativeEnvelope(
            "epdesk.extraction.v1",
            job.PipelineVersion,
            generatedAtUtc,
            new DerivativeSource(
                job.DocumentId,
                job.DocumentVersionId,
                job.VersionNumber,
                job.SourceType,
                job.SourceRecordId,
                job.DeviceCode,
                job.FileName,
                job.BucketName,
                job.ObjectKey,
                downloaded.VersionId,
                downloaded.ETag,
                downloaded.SizeBytes,
                downloaded.Sha256,
                inspection.DetectedContentType,
                job.Classification,
                job.Department
            ),
            extracted.Format,
            extracted.Metadata,
            extracted.Warnings,
            extracted.Sections
        );

        await using (var stream = new FileStream(
            derivativeFilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                envelope,
                JsonOptions,
                cancellationToken
            );
            await stream.FlushAsync(cancellationToken);
        }

        var pageCount = TryReadInteger(extracted.Metadata, "page_count");
        var summaryJson = JsonSerializer.Serialize(
            new
            {
                schemaVersion = "epdesk.extraction.v1",
                extracted.Format,
                sectionCount = sections.Length,
                pageCount,
                extracted.Metadata,
                extracted.Warnings,
                generatedAtUtc
            },
            JsonOptions
        );

        var pipelinePath = NormalizePipelinePath(job.PipelineVersion);
        var derivativePrefix =
            $"derivatives/{job.DocumentId:N}/{job.DocumentVersionId:N}/{pipelinePath}";

        return new PreparedExtractionArtifact(
            sections,
            pageCount,
            summaryJson,
            derivativeFilePath,
            $"{derivativePrefix}/extraction.json"
        );
    }

    public static SuccessfulExtraction BuildSuccessfulExtraction(
        PreparedExtractionArtifact prepared,
        FileInspection inspection,
        DownloadedObject downloaded,
        UploadedObject uploaded)
    {
        return new SuccessfulExtraction
        {
            DetectedContentType = inspection.DetectedContentType,
            SourceB2VersionId = downloaded.VersionId,
            SourceObjectETag = downloaded.ETag,
            Sha256 = downloaded.Sha256,
            PageCount = prepared.PageCount,
            MetadataJson = prepared.SummaryMetadataJson,
            Sections = prepared.Sections,
            Derivatives =
            [
                new StoredDocumentDerivative
                {
                    Kind = "structured_json",
                    Ordinal = 0,
                    BucketName = uploaded.BucketName,
                    ObjectKey = uploaded.ObjectKey,
                    B2VersionId = uploaded.VersionId,
                    ObjectETag = uploaded.ETag,
                    ContentType = uploaded.ContentType,
                    SizeBytes = uploaded.SizeBytes,
                    Sha256 = uploaded.Sha256,
                    MetadataJson = JsonSerializer.Serialize(
                        new
                        {
                            schemaVersion = "epdesk.extraction.v1",
                            sectionCount = prepared.Sections.Count
                        },
                        JsonOptions
                    )
                }
            ]
        };
    }

    private static ExtractedDocumentSection MapSection(
        ClaimedExtractionJob job,
        ExtractedSection source,
        int ordinal)
    {
        var sectionType = Truncate(
            string.IsNullOrWhiteSpace(source.SectionType) ? "section" : source.SectionType.Trim(),
            64
        );
        var heading = Truncate(source.Heading?.Trim() ?? "", 1000);
        var isOcr = sectionType.Contains("ocr", StringComparison.OrdinalIgnoreCase);
        var content = source.Content ?? "";
        var locatorJson = JsonSerializer.Serialize(
            new
            {
                fileId = job.DocumentId,
                versionId = job.DocumentVersionId,
                fileName = job.FileName,
                sourceType = job.SourceType,
                sourceRecordId = job.SourceRecordId,
                sectionType,
                sectionNumber = source.SectionNumber,
                heading
            },
            JsonOptions
        );
        var metadataJson = JsonSerializer.Serialize(source.Metadata, JsonOptions);

        return new ExtractedDocumentSection
        {
            Ordinal = ordinal,
            SectionType = sectionType,
            SectionNumber = source.SectionNumber,
            Heading = heading,
            Content = isOcr ? "" : content,
            OcrContent = isOcr ? content : "",
            ContentHash = CreateContentHash(
                sectionType,
                source.SectionNumber,
                heading,
                content
            ),
            CharacterCount = content.Length,
            TokenCount = null,
            Language = "",
            LocatorJson = locatorJson,
            MetadataJson = metadataJson
        };
    }

    private static IReadOnlyList<ExtractedSection> ExpandSearchSections(
        IReadOnlyList<ExtractedSection> sourceSections)
    {
        var expanded = new List<ExtractedSection>();
        foreach (var source in sourceSections)
        {
            var chunks = ChunkText(source.Content ?? "");
            if (chunks.Count == 1)
            {
                expanded.Add(source);
                continue;
            }

            for (var index = 0; index < chunks.Count; index++)
            {
                var metadata = new Dictionary<string, object?>(source.Metadata)
                {
                    ["chunk_index"] = index + 1,
                    ["chunk_count"] = chunks.Count
                };
                expanded.Add(new ExtractedSection(
                    source.SectionType,
                    source.SectionNumber,
                    source.Heading,
                    chunks[index],
                    metadata
                ));
            }
        }

        return expanded;
    }

    private static IReadOnlyList<string> ChunkText(string content)
    {
        if (content.Length <= MaximumSearchSectionCharacters)
        {
            return [content];
        }

        var chunks = new List<string>();
        var offset = 0;
        while (offset < content.Length)
        {
            var length = Math.Min(
                MaximumSearchSectionCharacters,
                content.Length - offset
            );
            if (offset + length < content.Length)
            {
                var searchStart = offset + (length * 3 / 4);
                var breakAt = content.LastIndexOf(
                    '\n',
                    offset + length - 1,
                    offset + length - searchStart
                );
                if (breakAt >= searchStart)
                {
                    length = breakAt - offset + 1;
                }

                if (char.IsHighSurrogate(content[offset + length - 1]))
                {
                    length--;
                }
            }

            chunks.Add(content.Substring(offset, length));
            offset += length;
        }

        return chunks;
    }

    private static string CreateContentHash(
        string sectionType,
        int? sectionNumber,
        string heading,
        string content)
    {
        var values = new[]
        {
            sectionType,
            sectionNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            heading,
            content
        };
        var canonical = string.Concat(values.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))
        ).ToLowerInvariant();
    }

    private static int? TryReadInteger(
        IReadOnlyDictionary<string, object?> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int integer => integer,
            long integer when integer is >= int.MinValue and <= int.MaxValue => (int)integer,
            JsonElement { ValueKind: JsonValueKind.Number } element
                when element.TryGetInt32(out var integer) => integer,
            _ => null
        };
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string NormalizePipelinePath(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 64 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("Pipeline version is unsafe for a derivative key.");
        }

        return value;
    }

    private sealed record StructuredDerivativeEnvelope(
        string SchemaVersion,
        string PipelineVersion,
        DateTime GeneratedAtUtc,
        DerivativeSource Source,
        string Format,
        IReadOnlyDictionary<string, object?> Metadata,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<ExtractedSection> Sections
    );

    private sealed record DerivativeSource(
        Guid FileId,
        Guid VersionId,
        int VersionNumber,
        string SourceType,
        Guid SourceRecordId,
        string DeviceCode,
        string FileName,
        string BucketName,
        string ObjectKey,
        string B2VersionId,
        string ObjectETag,
        long SizeBytes,
        string Sha256,
        string ContentType,
        string Classification,
        string Department
    );
}

public sealed record PreparedExtractionArtifact(
    IReadOnlyList<ExtractedDocumentSection> Sections,
    int? PageCount,
    string SummaryMetadataJson,
    string FilePath,
    string ObjectKey
);
