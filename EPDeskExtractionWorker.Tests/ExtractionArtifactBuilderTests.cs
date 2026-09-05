using System.Text.Json;
using EPDeskExtractionWorker.Services.Extraction;
using EPDeskExtractionWorker.Services.Jobs;
using EPDeskExtractionWorker.Services.Processing;

namespace EPDeskExtractionWorker.Tests;

public sealed class ExtractionArtifactBuilderTests
{
    [Fact]
    public async Task PrepareAndBuild_MapSectionsAndStructuredDerivativeMetadata()
    {
        using var files = new TestFiles();
        var job = CreateJob();
        var extracted = new ExtractedDocument(
            "pdf",
            [
                new ExtractedSection(
                    "page",
                    4,
                    "Commercial Terms",
                    "Payment is due in 30 days.",
                    new Dictionary<string, object?> { ["bbox"] = "0,0,100,100" }),
                new ExtractedSection(
                    "ocr_page",
                    5,
                    null,
                    "Scanned approval",
                    new Dictionary<string, object?>())
            ],
            new Dictionary<string, object?> { ["page_count"] = 5 },
            ["OCR was required on page 5."]);
        var inspection = new FileInspection(".pdf", "application/pdf", "application/pdf");
        var downloaded = new DownloadedObject(
            Path.Combine(files.Root, "source.pdf"),
            128,
            new string('a', 64),
            "b2-source-version",
            "source-etag");
        var derivativePath = Path.Combine(files.Root, "extraction.json");

        var prepared = await new ExtractionArtifactBuilder().PrepareAsync(
            job,
            extracted,
            inspection,
            downloaded,
            derivativePath,
            CancellationToken.None);

        Assert.Equal(5, prepared.PageCount);
        Assert.Equal(
            "derivatives/11111111111111111111111111111111/22222222222222222222222222222222/v1/extraction.json",
            prepared.ObjectKey);
        Assert.True(File.Exists(prepared.FilePath));
        Assert.Equal("Payment is due in 30 days.", prepared.Sections[0].Content);
        Assert.Empty(prepared.Sections[0].OcrContent);
        Assert.Empty(prepared.Sections[1].Content);
        Assert.Equal("Scanned approval", prepared.Sections[1].OcrContent);
        Assert.Equal(64, prepared.Sections[0].ContentHash.Length);
        Assert.Contains(job.DocumentId.ToString(), prepared.Sections[0].LocatorJson);

        using (var derivative = JsonDocument.Parse(await File.ReadAllTextAsync(derivativePath)))
        {
            Assert.Equal("epdesk.extraction.v1", derivative.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("proposal.pdf", derivative.RootElement.GetProperty("source").GetProperty("file_name").GetString());
            Assert.Equal(2, derivative.RootElement.GetProperty("sections").GetArrayLength());
        }

        var successful = ExtractionArtifactBuilder.BuildSuccessfulExtraction(
            prepared,
            inspection,
            downloaded,
            new UploadedObject(
                "derivatives-bucket",
                prepared.ObjectKey,
                "b2-derivative-version",
                "derivative-etag",
                512,
                new string('b', 64),
                "application/json"));

        Assert.Equal("application/pdf", successful.DetectedContentType);
        Assert.Equal(downloaded.VersionId, successful.SourceB2VersionId);
        Assert.Equal(downloaded.ETag, successful.SourceObjectETag);
        Assert.Equal(downloaded.Sha256, successful.Sha256);
        Assert.Equal(2, successful.Sections.Count);
        var stored = Assert.Single(successful.Derivatives);
        Assert.Equal("structured_json", stored.Kind);
        Assert.Equal("derivatives-bucket", stored.BucketName);
        Assert.Equal(prepared.ObjectKey, stored.ObjectKey);
    }

    [Fact]
    public async Task Prepare_ChunksLargeSearchSectionsBeforePostgresIndexing()
    {
        using var files = new TestFiles();
        var content = string.Concat(Enumerable.Repeat("Large searchable paragraph.\n", 5_000));
        var extracted = new ExtractedDocument(
            "text",
            [
                new ExtractedSection(
                    "text",
                    1,
                    null,
                    content,
                    new Dictionary<string, object?>()
                )
            ],
            new Dictionary<string, object?>(),
            []
        );

        var prepared = await new ExtractionArtifactBuilder().PrepareAsync(
            CreateJob() with { FileName = "large.txt", DeclaredContentType = "text/plain" },
            extracted,
            new FileInspection(".txt", "text/plain", "text/plain"),
            new DownloadedObject(
                Path.Combine(files.Root, "large.txt"),
                content.Length,
                new string('a', 64),
                "version",
                "etag"
            ),
            Path.Combine(files.Root, "large-extraction.json"),
            CancellationToken.None
        );

        Assert.True(prepared.Sections.Count > 1);
        Assert.All(prepared.Sections, section =>
            Assert.InRange(section.CharacterCount, 1, 50_000));
        Assert.Equal(content, string.Concat(prepared.Sections.Select(section => section.Content)));
    }

    private static ClaimedExtractionJob CreateJob() => new()
    {
        JobId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        DocumentVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PipelineVersion = "v1",
        Priority = 0,
        AttemptCount = 1,
        MaxAttempts = 5,
        LeaseOwner = "test-worker",
        LeaseToken = "lease-token",
        LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5),
        DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        VersionNumber = 1,
        SourceVersionKey = "source-v1",
        FileName = "proposal.pdf",
        FileExtension = ".pdf",
        BucketName = "private-source",
        ObjectKey = "devices/test/proposal.pdf",
        B2VersionId = "b2-source-version",
        ObjectETag = "source-etag",
        SizeBytes = 128,
        SourceModifiedAtUtc = DateTime.UtcNow,
        DeclaredContentType = "application/pdf",
        Sha256 = "",
        DerivativePrefix = "",
        SourceType = "automatic_upload",
        SourceRecordId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        DeviceCode = "device-1",
        DisplayName = "Proposal",
        Classification = "confidential",
        Department = "sales",
        IsDeleted = false
    };
}
