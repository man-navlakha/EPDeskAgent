using EPDeskExtractionWorker.Services;
using EPDeskExtractionWorker.Services.Jobs;
using EPDeskExtractionWorker.Services.Processing;

namespace EPDeskExtractionWorker.Tests;

public sealed class ExtractionWorkerIdentityTests
{
    [Fact]
    public void VerifyObjectIdentity_UnpinnedSourceWithoutReturnedVersion_IsRetryable()
    {
        var exception = Assert.Throws<RetryableExtractionException>(() =>
            ExtractionWorker.VerifyObjectIdentity(
                CreateJob() with { B2VersionId = "", ObjectETag = "" },
                CreateDownload() with { VersionId = "", ETag = "" }
            )
        );

        Assert.Equal("source_version_identity_missing", exception.ErrorCode);
    }

    [Fact]
    public void VerifyObjectIdentity_UnpinnedSourceLearnsReturnedIdentity()
    {
        ExtractionWorker.VerifyObjectIdentity(
            CreateJob() with { B2VersionId = "", ObjectETag = "" },
            CreateDownload()
        );
    }

    [Fact]
    public void VerifyObjectIdentity_PinnedSourceStillRequiresExactVersion()
    {
        var exception = Assert.Throws<RetryableExtractionException>(() =>
            ExtractionWorker.VerifyObjectIdentity(
                CreateJob(),
                CreateDownload() with { VersionId = "different-version" }
            )
        );

        Assert.Equal("source_version_mismatch", exception.ErrorCode);
    }

    private static DownloadedObject CreateDownload() => new(
        "source.pdf",
        128,
        new string('a', 64),
        "returned-version",
        "source-etag"
    );

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
        FileName = "source.pdf",
        FileExtension = ".pdf",
        BucketName = "private-source",
        ObjectKey = "devices/test/source.pdf",
        B2VersionId = "returned-version",
        ObjectETag = "source-etag",
        SizeBytes = 128,
        SourceModifiedAtUtc = DateTime.UtcNow,
        DeclaredContentType = "application/pdf",
        Sha256 = "",
        DerivativePrefix = "",
        SourceType = "automatic_upload",
        SourceRecordId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        DeviceCode = "device-1",
        DisplayName = "Source",
        Classification = "confidential",
        Department = "sales",
        IsDeleted = false
    };
}
