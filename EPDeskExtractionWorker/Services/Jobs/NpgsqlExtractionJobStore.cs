using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace EPDeskExtractionWorker.Services.Jobs;

/// <summary>
/// PostgreSQL-backed extraction queue. Every mutation after claim is guarded
/// by both job ID and lease token, and also requires an unexpired lease.
/// </summary>
public sealed class NpgsqlExtractionJobStore(NpgsqlDataSource dataSource) :
    IExtractionJobStore
{
    private const int ExpiredJobSweepLimit = 100;

    public async Task<ClaimedExtractionJob?> TryClaimAsync(
        ClaimExtractionJobRequest request,
        CancellationToken cancellationToken
    )
    {
        ValidateClaimRequest(request);

        var leaseToken = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)
        ).ToLowerInvariant();

        await using var connection = await dataSource.OpenConnectionAsync(
            cancellationToken
        );
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken
        );

        await SweepExpiredFinalAttemptsAsync(
            connection,
            transaction,
            request.PipelineVersion,
            cancellationToken
        );

        const string sql =
            """
            WITH candidate AS
            (
                SELECT job."Id"
                FROM "ExtractionJobs" AS job
                INNER JOIN "DocumentVersions" AS candidate_version
                    ON candidate_version."Id" = job."DocumentVersionId"
                WHERE job."PipelineVersion" = @pipeline_version
                  AND (@allowed_job_id IS NULL OR job."Id" = @allowed_job_id)
                  AND candidate_version."FileExtension" = ANY(@allowed_extensions)
                  AND
                  (
                      NOT @require_trusted_source_identity
                      OR
                      (
                          candidate_version."B2VersionId" <> ''
                          AND candidate_version."Sha256" ~ '^[0-9a-fA-F]{64}$'
                      )
                  )
                  AND job."AttemptCount" < job."MaxAttempts"
                  AND
                  (
                      (
                          job."Status" IN ('queued', 'retry_wait')
                          AND job."NextAttemptAtUtc" <= clock_timestamp()
                      )
                      OR
                      (
                          job."Status" = 'running'
                          AND job."LeaseUntilUtc" IS NOT NULL
                          AND job."LeaseUntilUtc" <= clock_timestamp()
                      )
                  )
                ORDER BY
                    job."Priority" DESC,
                    job."NextAttemptAtUtc" ASC,
                    job."CreatedAtUtc" ASC
                LIMIT 1
                FOR UPDATE OF job SKIP LOCKED
            ),
            claimed AS
            (
                UPDATE "ExtractionJobs" AS job
                SET "Status" = 'running',
                    "AttemptCount" = job."AttemptCount" + 1,
                    "LeaseOwner" = @lease_owner,
                    "LeaseToken" = @lease_token,
                    "LeaseUntilUtc" = clock_timestamp() + @lease_duration,
                    "LastHeartbeatAtUtc" = clock_timestamp(),
                    "UpdatedAtUtc" = clock_timestamp(),
                    "StartedAtUtc" = COALESCE(
                        job."StartedAtUtc",
                        clock_timestamp()
                    ),
                    "LastAttemptAtUtc" = clock_timestamp(),
                    "CompletedAtUtc" = NULL,
                    "ErrorCode" = '',
                    "ErrorMessage" = ''
                FROM candidate
                WHERE job."Id" = candidate."Id"
                RETURNING job.*
            )
            SELECT
                claimed."Id" AS job_id,
                claimed."DocumentVersionId" AS document_version_id,
                claimed."PipelineVersion" AS pipeline_version,
                claimed."Priority" AS priority,
                claimed."AttemptCount" AS attempt_count,
                claimed."MaxAttempts" AS max_attempts,
                claimed."LeaseOwner" AS lease_owner,
                claimed."LeaseToken" AS lease_token,
                claimed."LeaseUntilUtc" AS lease_until_utc,
                version."DocumentId" AS document_id,
                version."VersionNumber" AS version_number,
                version."SourceVersionKey" AS source_version_key,
                version."FileName" AS file_name,
                version."FileExtension" AS file_extension,
                version."BucketName" AS bucket_name,
                version."ObjectKey" AS object_key,
                version."B2VersionId" AS b2_version_id,
                version."ObjectETag" AS object_etag,
                version."SizeBytes" AS size_bytes,
                version."SourceModifiedAtUtc" AS source_modified_at_utc,
                version."DeclaredContentType" AS declared_content_type,
                version."Sha256" AS sha256,
                version."DerivativePrefix" AS derivative_prefix,
                document."SourceType" AS source_type,
                document."SourceRecordId" AS source_record_id,
                document."DeviceCode" AS device_code,
                document."DisplayName" AS display_name,
                document."Classification" AS classification,
                document."Department" AS department,
                document."IsDeleted" AS is_deleted
            FROM claimed
            INNER JOIN "DocumentVersions" AS version
                ON version."Id" = claimed."DocumentVersionId"
            INNER JOIN "Documents" AS document
                ON document."Id" = version."DocumentId";
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue(
            "pipeline_version",
            request.PipelineVersion
        );
        command.Parameters.AddWithValue("lease_owner", request.LeaseOwner);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_duration", request.LeaseDuration);
        command.Parameters.Add("allowed_job_id", NpgsqlDbType.Uuid).Value =
            request.AllowedJobId.HasValue
                ? request.AllowedJobId.Value
                : DBNull.Value;
        command.Parameters.AddWithValue(
            "require_trusted_source_identity",
            request.RequireTrustedSourceIdentity
        );
        command.Parameters.Add(
            "allowed_extensions",
            NpgsqlDbType.Array | NpgsqlDbType.Text
        ).Value = request.AllowedExtensions.ToArray();

        ClaimedExtractionJob? claimedJob = null;
        await using (var reader = await command.ExecuteReaderAsync(
            cancellationToken
        ))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                claimedJob = ReadClaimedJob(reader);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimedJob;
    }

    public async Task<bool> HeartbeatAsync(
        ExtractionJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        ValidateLease(lease);
        ValidateLeaseDuration(leaseDuration);

        const string sql =
            """
            UPDATE "ExtractionJobs"
            SET "LeaseUntilUtc" = clock_timestamp() + @lease_duration,
                "LastHeartbeatAtUtc" = clock_timestamp(),
                "UpdatedAtUtc" = clock_timestamp()
            WHERE "Id" = @job_id
              AND "LeaseToken" = @lease_token
              AND "Status" = 'running'
              AND "LeaseUntilUtc" > clock_timestamp();
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("job_id", lease.JobId);
        command.Parameters.AddWithValue("lease_token", lease.LeaseToken);
        command.Parameters.AddWithValue("lease_duration", leaseDuration);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkProcessingAsync(
        ExtractionJobLease lease,
        CancellationToken cancellationToken
    )
    {
        ValidateLease(lease);

        const string sql =
            """
            WITH owned_job AS
            (
                SELECT
                    "DocumentVersionId",
                    "PipelineVersion"
                FROM "ExtractionJobs"
                WHERE "Id" = @job_id
                  AND "LeaseToken" = @lease_token
                  AND "Status" = 'running'
                  AND "LeaseUntilUtc" > clock_timestamp()
                FOR UPDATE
            )
            UPDATE "DocumentVersions" AS version
            SET "ExtractionStatus" = 'processing',
                "ExtractionErrorCode" = '',
                "ExtractionError" = '',
                "UpdatedAtUtc" = clock_timestamp()
            FROM owned_job
            WHERE version."Id" = owned_job."DocumentVersionId";
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("job_id", lease.JobId);
        command.Parameters.AddWithValue("lease_token", lease.LeaseToken);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> CompleteAsync(
        ExtractionJobLease lease,
        SuccessfulExtraction extraction,
        CancellationToken cancellationToken
    )
    {
        ValidateLease(lease);
        ValidateSuccessfulExtraction(extraction);

        await using var connection = await dataSource.OpenConnectionAsync(
            cancellationToken
        );
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken
        );

        var ownedJob = await TryLockOwnedJobAsync(
            connection,
            transaction,
            lease,
            cancellationToken
        );
        if (ownedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await DeleteExistingOutputsAsync(
            connection,
            transaction,
            ownedJob,
            cancellationToken
        );

        foreach (var section in extraction.Sections)
        {
            await InsertSectionAsync(
                connection,
                transaction,
                ownedJob,
                section,
                cancellationToken
            );
        }

        foreach (var derivative in extraction.Derivatives)
        {
            await InsertDerivativeAsync(
                connection,
                transaction,
                ownedJob,
                derivative,
                cancellationToken
            );
        }

        await UpdateCompletedVersionAsync(
            connection,
            transaction,
            ownedJob,
            extraction,
            cancellationToken
        );

        var jobUpdated = await UpdateCompletedJobAsync(
            connection,
            transaction,
            lease,
            cancellationToken
        );
        if (!jobUpdated)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ExtractionFailureTransition?> FailAsync(
        ExtractionJobLease lease,
        ExtractionFailure failure,
        CancellationToken cancellationToken
    )
    {
        ValidateLease(lease);
        ValidateFailure(failure);

        await using var connection = await dataSource.OpenConnectionAsync(
            cancellationToken
        );
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken
        );

        var ownedJob = await TryLockOwnedJobAsync(
            connection,
            transaction,
            lease,
            cancellationToken
        );
        if (ownedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var status = ResolveFailureStatus(ownedJob, failure.Disposition);
        var retryDelay = status == ExtractionJobStatuses.RetryWait
            ? CalculateRetryDelay(
                ownedJob.AttemptCount,
                failure.InitialRetryDelay,
                failure.MaximumRetryDelay
            )
            : TimeSpan.Zero;

        const string updateJobSql =
            """
            UPDATE "ExtractionJobs"
            SET "Status" = @status,
                "AttemptCount" = CASE
                    WHEN @release_attempt THEN GREATEST("AttemptCount" - 1, 0)
                    ELSE "AttemptCount"
                END,
                "LeaseOwner" = '',
                "LeaseToken" = '',
                "LeaseUntilUtc" = NULL,
                "NextAttemptAtUtc" = CASE
                    WHEN @is_retry THEN clock_timestamp() + @retry_delay
                    ELSE clock_timestamp()
                END,
                "ErrorCode" = @error_code,
                "ErrorMessage" = @error_message,
                "UpdatedAtUtc" = clock_timestamp(),
                "CompletedAtUtc" = CASE
                    WHEN @is_retry THEN NULL
                    ELSE clock_timestamp()
                END
            WHERE "Id" = @job_id
              AND "LeaseToken" = @lease_token
              AND "Status" = 'running'
            RETURNING "NextAttemptAtUtc";
            """;

        DateTime nextAttemptAtUtc;
        await using (var updateJob = new NpgsqlCommand(
            updateJobSql,
            connection,
            transaction
        ))
        {
            updateJob.Parameters.AddWithValue("status", status);
            updateJob.Parameters.AddWithValue(
                "release_attempt",
                failure.Disposition == ExtractionFailureDisposition.Release
            );
            updateJob.Parameters.AddWithValue(
                "is_retry",
                status == ExtractionJobStatuses.RetryWait
            );
            updateJob.Parameters.AddWithValue("retry_delay", retryDelay);
            updateJob.Parameters.AddWithValue("error_code", failure.ErrorCode);
            updateJob.Parameters.AddWithValue(
                "error_message",
                failure.ErrorMessage
            );
            updateJob.Parameters.AddWithValue("job_id", lease.JobId);
            updateJob.Parameters.AddWithValue(
                "lease_token",
                lease.LeaseToken
            );

            var result = await updateJob.ExecuteScalarAsync(cancellationToken);
            if (result is not DateTime nextAttempt)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            nextAttemptAtUtc = nextAttempt;
        }

        var versionStatus = status switch
        {
            ExtractionJobStatuses.RetryWait => "queued",
            ExtractionJobStatuses.Rejected => "rejected",
            _ => "failed"
        };

        const string updateVersionSql =
            """
            UPDATE "DocumentVersions"
            SET "ExtractionStatus" = @version_status,
                "ExtractionErrorCode" = @error_code,
                "ExtractionError" = @error_message,
                "UpdatedAtUtc" = clock_timestamp()
            WHERE "Id" = @document_version_id;
            """;

        await using (var updateVersion = new NpgsqlCommand(
            updateVersionSql,
            connection,
            transaction
        ))
        {
            updateVersion.Parameters.AddWithValue(
                "version_status",
                versionStatus
            );
            updateVersion.Parameters.AddWithValue(
                "error_code",
                failure.ErrorCode
            );
            updateVersion.Parameters.AddWithValue(
                "error_message",
                failure.ErrorMessage
            );
            updateVersion.Parameters.AddWithValue(
                "document_version_id",
                ownedJob.DocumentVersionId
            );
            await updateVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ExtractionFailureTransition
        {
            Status = status,
            AttemptCount = failure.Disposition == ExtractionFailureDisposition.Release
                ? Math.Max(0, ownedJob.AttemptCount - 1)
                : ownedJob.AttemptCount,
            MaxAttempts = ownedJob.MaxAttempts,
            NextAttemptAtUtc = status == ExtractionJobStatuses.RetryWait
                ? nextAttemptAtUtc
                : null
        };
    }

    private static async Task SweepExpiredFinalAttemptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string pipelineVersion,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            WITH expired AS
            (
                SELECT
                    job."Id",
                    job."DocumentVersionId",
                    job."PipelineVersion"
                FROM "ExtractionJobs" AS job
                WHERE job."Status" = 'running'
                  AND job."PipelineVersion" = @pipeline_version
                  AND job."LeaseUntilUtc" IS NOT NULL
                  AND job."LeaseUntilUtc" <= clock_timestamp()
                  AND job."AttemptCount" >= job."MaxAttempts"
                ORDER BY job."LeaseUntilUtc" ASC
                LIMIT @sweep_limit
                FOR UPDATE OF job SKIP LOCKED
            ),
            dead_jobs AS
            (
                UPDATE "ExtractionJobs" AS job
                SET "Status" = 'dead_letter',
                    "LeaseOwner" = '',
                    "LeaseToken" = '',
                    "LeaseUntilUtc" = NULL,
                    "ErrorCode" = 'lease_expired',
                    "ErrorMessage" =
                        'The final processing attempt expired before completion.',
                    "UpdatedAtUtc" = clock_timestamp(),
                    "CompletedAtUtc" = clock_timestamp()
                FROM expired
                WHERE job."Id" = expired."Id"
                RETURNING
                    job."DocumentVersionId",
                    job."PipelineVersion"
            )
            UPDATE "DocumentVersions" AS version
            SET "ExtractionStatus" = 'failed',
                "ExtractionErrorCode" = 'lease_expired',
                "ExtractionError" =
                    'The final processing attempt expired before completion.',
                "UpdatedAtUtc" = clock_timestamp()
            FROM dead_jobs
            WHERE version."Id" = dead_jobs."DocumentVersionId";
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("sweep_limit", ExpiredJobSweepLimit);
        command.Parameters.AddWithValue("pipeline_version", pipelineVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<OwnedJob?> TryLockOwnedJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExtractionJobLease lease,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            SELECT
                "DocumentVersionId",
                "PipelineVersion",
                "AttemptCount",
                "MaxAttempts"
            FROM "ExtractionJobs"
            WHERE "Id" = @job_id
              AND "LeaseToken" = @lease_token
              AND "Status" = 'running'
              AND "LeaseUntilUtc" > clock_timestamp()
            FOR UPDATE;
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("job_id", lease.JobId);
        command.Parameters.AddWithValue("lease_token", lease.LeaseToken);

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken
        );
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OwnedJob(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3)
        );
    }

    private static async Task DeleteExistingOutputsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedJob ownedJob,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            DELETE FROM "DocumentSections"
            WHERE "DocumentVersionId" = @document_version_id
              AND "PipelineVersion" = @pipeline_version;

            DELETE FROM "DocumentDerivatives"
            WHERE "DocumentVersionId" = @document_version_id
              AND "PipelineVersion" = @pipeline_version;
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue(
            "document_version_id",
            ownedJob.DocumentVersionId
        );
        command.Parameters.AddWithValue(
            "pipeline_version",
            ownedJob.PipelineVersion
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedJob ownedJob,
        ExtractedDocumentSection section,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            INSERT INTO "DocumentSections"
            (
                "Id",
                "DocumentVersionId",
                "PipelineVersion",
                "Ordinal",
                "SectionType",
                "SectionNumber",
                "Heading",
                "Content",
                "OcrContent",
                "ContentHash",
                "CharacterCount",
                "TokenCount",
                "Language",
                "LocatorJson",
                "MetadataJson",
                "CreatedAtUtc"
            )
            VALUES
            (
                @id,
                @document_version_id,
                @pipeline_version,
                @ordinal,
                @section_type,
                @section_number,
                @heading,
                @content,
                @ocr_content,
                @content_hash,
                @character_count,
                @token_count,
                @language,
                @locator_json,
                @metadata_json,
                clock_timestamp()
            );
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "document_version_id",
            ownedJob.DocumentVersionId
        );
        command.Parameters.AddWithValue(
            "pipeline_version",
            ownedJob.PipelineVersion
        );
        command.Parameters.AddWithValue("ordinal", section.Ordinal);
        command.Parameters.AddWithValue("section_type", section.SectionType);
        AddNullableParameter(
            command,
            "section_number",
            NpgsqlDbType.Integer,
            section.SectionNumber
        );
        command.Parameters.AddWithValue("heading", section.Heading);
        command.Parameters.AddWithValue("content", section.Content);
        command.Parameters.AddWithValue("ocr_content", section.OcrContent);
        command.Parameters.AddWithValue("content_hash", section.ContentHash);
        command.Parameters.AddWithValue(
            "character_count",
            section.CharacterCount
        );
        AddNullableParameter(
            command,
            "token_count",
            NpgsqlDbType.Integer,
            section.TokenCount
        );
        command.Parameters.AddWithValue("language", section.Language);
        command.Parameters.Add(
            "locator_json",
            NpgsqlDbType.Jsonb
        ).Value = section.LocatorJson;
        command.Parameters.Add(
            "metadata_json",
            NpgsqlDbType.Jsonb
        ).Value = section.MetadataJson;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDerivativeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedJob ownedJob,
        StoredDocumentDerivative derivative,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            INSERT INTO "DocumentDerivatives"
            (
                "Id",
                "DocumentVersionId",
                "PipelineVersion",
                "Kind",
                "Ordinal",
                "BucketName",
                "ObjectKey",
                "B2VersionId",
                "ObjectETag",
                "ContentType",
                "SizeBytes",
                "Sha256",
                "MetadataJson",
                "CreatedAtUtc"
            )
            VALUES
            (
                @id,
                @document_version_id,
                @pipeline_version,
                @kind,
                @ordinal,
                @bucket_name,
                @object_key,
                @b2_version_id,
                @object_etag,
                @content_type,
                @size_bytes,
                @sha256,
                @metadata_json,
                clock_timestamp()
            );
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "document_version_id",
            ownedJob.DocumentVersionId
        );
        command.Parameters.AddWithValue(
            "pipeline_version",
            ownedJob.PipelineVersion
        );
        command.Parameters.AddWithValue("kind", derivative.Kind);
        command.Parameters.AddWithValue("ordinal", derivative.Ordinal);
        command.Parameters.AddWithValue("bucket_name", derivative.BucketName);
        command.Parameters.AddWithValue("object_key", derivative.ObjectKey);
        command.Parameters.AddWithValue(
            "b2_version_id",
            derivative.B2VersionId
        );
        command.Parameters.AddWithValue("object_etag", derivative.ObjectETag);
        command.Parameters.AddWithValue("content_type", derivative.ContentType);
        command.Parameters.AddWithValue("size_bytes", derivative.SizeBytes);
        command.Parameters.AddWithValue("sha256", derivative.Sha256);
        command.Parameters.Add(
            "metadata_json",
            NpgsqlDbType.Jsonb
        ).Value = derivative.MetadataJson;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateCompletedVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedJob ownedJob,
        SuccessfulExtraction extraction,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            UPDATE "DocumentVersions"
            SET "DetectedContentType" = @detected_content_type,
                "B2VersionId" = CASE
                    WHEN "B2VersionId" = '' THEN @source_b2_version_id
                    ELSE "B2VersionId"
                END,
                "ObjectETag" = CASE
                    WHEN "ObjectETag" = '' THEN @source_object_etag
                    ELSE "ObjectETag"
                END,
                "Sha256" = @sha256,
                "ExtractionStatus" = 'completed',
                "PageCount" = @page_count,
                "SectionCount" = @section_count,
                "ExtractionPipelineVersion" = @pipeline_version,
                "ExtractionMetadataJson" = @metadata_json,
                "ExtractionErrorCode" = '',
                "ExtractionError" = '',
                "UpdatedAtUtc" = clock_timestamp(),
                "ExtractedAtUtc" = clock_timestamp()
            WHERE "Id" = @document_version_id;
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue(
            "detected_content_type",
            extraction.DetectedContentType
        );
        command.Parameters.AddWithValue(
            "source_b2_version_id",
            extraction.SourceB2VersionId
        );
        command.Parameters.AddWithValue(
            "source_object_etag",
            extraction.SourceObjectETag
        );
        command.Parameters.AddWithValue("sha256", extraction.Sha256);
        AddNullableParameter(
            command,
            "page_count",
            NpgsqlDbType.Integer,
            extraction.PageCount
        );
        command.Parameters.AddWithValue(
            "section_count",
            extraction.Sections.Count
        );
        command.Parameters.AddWithValue(
            "pipeline_version",
            ownedJob.PipelineVersion
        );
        command.Parameters.Add(
            "metadata_json",
            NpgsqlDbType.Jsonb
        ).Value = extraction.MetadataJson;
        command.Parameters.AddWithValue(
            "document_version_id",
            ownedJob.DocumentVersionId
        );

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> UpdateCompletedJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExtractionJobLease lease,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            """
            UPDATE "ExtractionJobs"
            SET "Status" = 'completed',
                "LeaseOwner" = '',
                "LeaseToken" = '',
                "LeaseUntilUtc" = NULL,
                "ErrorCode" = '',
                "ErrorMessage" = '',
                "UpdatedAtUtc" = clock_timestamp(),
                "CompletedAtUtc" = clock_timestamp()
            WHERE "Id" = @job_id
              AND "LeaseToken" = @lease_token
              AND "Status" = 'running';
            """;

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction
        );
        command.Parameters.AddWithValue("job_id", lease.JobId);
        command.Parameters.AddWithValue("lease_token", lease.LeaseToken);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static ClaimedExtractionJob ReadClaimedJob(NpgsqlDataReader reader)
    {
        return new ClaimedExtractionJob
        {
            JobId = reader.GetGuid(reader.GetOrdinal("job_id")),
            DocumentVersionId = reader.GetGuid(
                reader.GetOrdinal("document_version_id")
            ),
            PipelineVersion = reader.GetString(
                reader.GetOrdinal("pipeline_version")
            ),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            AttemptCount = reader.GetInt32(
                reader.GetOrdinal("attempt_count")
            ),
            MaxAttempts = reader.GetInt32(reader.GetOrdinal("max_attempts")),
            LeaseOwner = reader.GetString(reader.GetOrdinal("lease_owner")),
            LeaseToken = reader.GetString(reader.GetOrdinal("lease_token")),
            LeaseUntilUtc = reader.GetDateTime(
                reader.GetOrdinal("lease_until_utc")
            ),
            DocumentId = reader.GetGuid(reader.GetOrdinal("document_id")),
            VersionNumber = reader.GetInt32(reader.GetOrdinal("version_number")),
            SourceVersionKey = reader.GetString(
                reader.GetOrdinal("source_version_key")
            ),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            FileExtension = reader.GetString(
                reader.GetOrdinal("file_extension")
            ),
            BucketName = reader.GetString(reader.GetOrdinal("bucket_name")),
            ObjectKey = reader.GetString(reader.GetOrdinal("object_key")),
            B2VersionId = reader.GetString(reader.GetOrdinal("b2_version_id")),
            ObjectETag = reader.GetString(reader.GetOrdinal("object_etag")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            SourceModifiedAtUtc = reader.IsDBNull(
                reader.GetOrdinal("source_modified_at_utc")
            )
                ? null
                : reader.GetDateTime(
                    reader.GetOrdinal("source_modified_at_utc")
                ),
            DeclaredContentType = reader.GetString(
                reader.GetOrdinal("declared_content_type")
            ),
            Sha256 = reader.GetString(reader.GetOrdinal("sha256")),
            DerivativePrefix = reader.GetString(
                reader.GetOrdinal("derivative_prefix")
            ),
            SourceType = reader.GetString(reader.GetOrdinal("source_type")),
            SourceRecordId = reader.GetGuid(
                reader.GetOrdinal("source_record_id")
            ),
            DeviceCode = reader.GetString(reader.GetOrdinal("device_code")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Classification = reader.GetString(
                reader.GetOrdinal("classification")
            ),
            Department = reader.GetString(reader.GetOrdinal("department")),
            IsDeleted = reader.GetBoolean(reader.GetOrdinal("is_deleted"))
        };
    }

    private static string ResolveFailureStatus(
        OwnedJob ownedJob,
        ExtractionFailureDisposition disposition
    )
    {
        return disposition switch
        {
            ExtractionFailureDisposition.Reject =>
                ExtractionJobStatuses.Rejected,
            ExtractionFailureDisposition.DeadLetter =>
                ExtractionJobStatuses.DeadLetter,
            ExtractionFailureDisposition.Release =>
                ExtractionJobStatuses.RetryWait,
            ExtractionFailureDisposition.Retry
                when ownedJob.AttemptCount < ownedJob.MaxAttempts =>
                    ExtractionJobStatuses.RetryWait,
            _ => ExtractionJobStatuses.DeadLetter
        };
    }

    private static TimeSpan CalculateRetryDelay(
        int attemptCount,
        TimeSpan initialDelay,
        TimeSpan maximumDelay
    )
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 30);
        var multiplier = Math.Pow(2, exponent);
        var ticks = Math.Min(
            initialDelay.Ticks * multiplier,
            maximumDelay.Ticks
        );

        return TimeSpan.FromTicks((long)ticks);
    }

    private static void AddNullableParameter<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        T? value
    ) where T : struct
    {
        command.Parameters.Add(name, type).Value = value.HasValue
            ? value.Value
            : DBNull.Value;
    }

    private static void ValidateClaimRequest(ClaimExtractionJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequiredText(request.LeaseOwner, 128, nameof(request.LeaseOwner));
        ValidateRequiredText(
            request.PipelineVersion,
            64,
            nameof(request.PipelineVersion)
        );
        ValidateLeaseDuration(request.LeaseDuration);
        if (request.AllowedExtensions.Count == 0 ||
            request.AllowedExtensions.Any(extension =>
                string.IsNullOrWhiteSpace(extension) || extension.Length > 32))
        {
            throw new ArgumentException(
                "At least one valid file extension is required.",
                nameof(request.AllowedExtensions)
            );
        }
        if (request.AllowedJobId == Guid.Empty)
        {
            throw new ArgumentException(
                "Allowed job ID cannot be empty.",
                nameof(request.AllowedJobId)
            );
        }
    }

    private static void ValidateLease(ExtractionJobLease lease)
    {
        if (lease.JobId == Guid.Empty)
        {
            throw new ArgumentException("Job ID is required.", nameof(lease));
        }

        ValidateRequiredText(lease.LeaseToken, 64, nameof(lease.LeaseToken));
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be greater than zero and no more than one day."
            );
        }
    }

    private static void ValidateSuccessfulExtraction(
        SuccessfulExtraction extraction
    )
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ValidateRequiredText(
            extraction.DetectedContentType,
            255,
            nameof(extraction.DetectedContentType)
        );
        ValidateRequiredText(
            extraction.SourceB2VersionId,
            256,
            nameof(extraction.SourceB2VersionId)
        );
        ValidateOptionalText(
            extraction.SourceObjectETag,
            256,
            nameof(extraction.SourceObjectETag)
        );
        ValidateSha256(extraction.Sha256, nameof(extraction.Sha256));
        ValidateJson(extraction.MetadataJson, nameof(extraction.MetadataJson));

        ArgumentNullException.ThrowIfNull(extraction.Sections);
        ArgumentNullException.ThrowIfNull(extraction.Derivatives);

        if (extraction.PageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraction.PageCount));
        }

        if (extraction.Sections.Select(section => section.Ordinal).Distinct().Count()
            != extraction.Sections.Count)
        {
            throw new ArgumentException(
                "Section ordinals must be unique within an extraction.",
                nameof(extraction)
            );
        }

        foreach (var section in extraction.Sections)
        {
            if (section.Ordinal < 0 || section.CharacterCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(extraction),
                    "Section ordinal and character count cannot be negative."
                );
            }

            if (section.SectionNumber < 0 || section.TokenCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(extraction),
                    "Section number and token count cannot be negative."
                );
            }

            ValidateRequiredText(section.SectionType, 64, "SectionType");
            ValidateOptionalText(section.Heading, 1000, "Heading");
            ValidateSha256(section.ContentHash, "ContentHash");
            ValidateOptionalText(section.Language, 32, "Language");
            ValidateJson(section.LocatorJson, "LocatorJson");
            ValidateJson(section.MetadataJson, "MetadataJson");
        }

        var duplicateDerivative = extraction.Derivatives
            .GroupBy(derivative => (derivative.Kind, derivative.Ordinal))
            .Any(group => group.Count() > 1);
        if (duplicateDerivative)
        {
            throw new ArgumentException(
                "Derivative kind and ordinal pairs must be unique.",
                nameof(extraction)
            );
        }

        foreach (var derivative in extraction.Derivatives)
        {
            if (derivative.Ordinal < 0 || derivative.SizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(extraction),
                    "Derivative ordinal and size cannot be negative."
                );
            }

            ValidateRequiredText(derivative.Kind, 64, "Kind");
            ValidateRequiredText(derivative.BucketName, 255, "BucketName");
            ValidateRequiredText(derivative.ObjectKey, null, "ObjectKey");
            ValidateOptionalText(derivative.B2VersionId, 256, "B2VersionId");
            ValidateOptionalText(derivative.ObjectETag, 256, "ObjectETag");
            ValidateRequiredText(derivative.ContentType, 255, "ContentType");
            ValidateSha256(derivative.Sha256, "Sha256");
            ValidateJson(derivative.MetadataJson, "MetadataJson");
        }
    }

    private static void ValidateFailure(ExtractionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ValidateRequiredText(failure.ErrorCode, 128, nameof(failure.ErrorCode));
        ValidateRequiredText(
            failure.ErrorMessage,
            null,
            nameof(failure.ErrorMessage)
        );

        if (failure.InitialRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failure.InitialRetryDelay)
            );
        }

        if (failure.MaximumRetryDelay < failure.InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failure.MaximumRetryDelay),
                "Maximum retry delay cannot be shorter than the initial delay."
            );
        }
    }

    private static void ValidateRequiredText(
        string value,
        int? maximumLength,
        string parameterName
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        ValidateOptionalText(value, maximumLength, parameterName);
    }

    private static void ValidateOptionalText(
        string value,
        int? maximumLength,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (maximumLength.HasValue && value.Length > maximumLength.Value)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength.Value} characters.",
                parameterName
            );
        }
    }

    private static void ValidateJson(string value, string parameterName)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Value must be valid JSON.",
                parameterName,
                exception
            );
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ValidateRequiredText(value, 64, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Value must be a 64-character hexadecimal SHA-256 digest.",
                parameterName
            );
        }
    }

    private sealed record OwnedJob(
        Guid DocumentVersionId,
        string PipelineVersion,
        int AttemptCount,
        int MaxAttempts
    );
}
