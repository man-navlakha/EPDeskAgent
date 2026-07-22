using Dapper;
using EPDeskAgent.Models;
using Microsoft.Data.Sqlite;

namespace EPDeskAgent.Database;

public class FileMetadataRepository
{
    private readonly LocalDatabase _database;

    public FileMetadataRepository(LocalDatabase database)
    {
        _database = database;
    }

    public async Task UpsertFileAsync(
        FileMetadata file,
        CancellationToken cancellationToken = default)
    {
        await UpsertFilesAsync([file], cancellationToken);
    }

    public async Task UpsertFilesAsync(
        IReadOnlyCollection<FileMetadata> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var sql =
        """
        INSERT INTO files (
            device_code,
            full_path,
            directory_path,
            file_name,
            extension,
            size_bytes,
            created_at_utc,
            updated_at_utc,
            last_seen_at_utc,
            is_deleted,
            sync_status
        )
        VALUES (
            @DeviceCode,
            @FullPath,
            @DirectoryPath,
            @FileName,
            @Extension,
            @SizeBytes,
            @CreatedAtUtc,
            @UpdatedAtUtc,
            @LastSeenAtUtc,
            0,
            'pending'
        )
        ON CONFLICT(full_path) DO UPDATE SET
            device_code = excluded.device_code,
            directory_path = excluded.directory_path,
            file_name = excluded.file_name,
            extension = excluded.extension,
            size_bytes = excluded.size_bytes,
            created_at_utc = excluded.created_at_utc,
            updated_at_utc = excluded.updated_at_utc,
            last_seen_at_utc = excluded.last_seen_at_utc,
            is_deleted = 0,
            sync_status = CASE
                WHEN files.device_code IS NOT excluded.device_code
                  OR files.directory_path IS NOT excluded.directory_path
                  OR files.file_name IS NOT excluded.file_name
                  OR files.extension IS NOT excluded.extension
                  OR files.size_bytes IS NOT excluded.size_bytes
                  OR files.created_at_utc IS NOT excluded.created_at_utc
                  OR files.updated_at_utc IS NOT excluded.updated_at_utc
                  OR files.is_deleted IS NOT 0
                THEN 'pending'
                ELSE files.sync_status
            END;
        """;

        try
        {
            await connection.ExecuteAsync(sql, files, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<FileMetadata>> GetPendingFilesAsync(int limit)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        var sql =
        """
        SELECT
            id AS Id,
            device_code AS DeviceCode,
            full_path AS FullPath,
            directory_path AS DirectoryPath,
            file_name AS FileName,
            extension AS Extension,
            size_bytes AS SizeBytes,
            created_at_utc AS CreatedAtUtc,
            updated_at_utc AS UpdatedAtUtc,
            last_seen_at_utc AS LastSeenAtUtc,
            is_deleted AS IsDeleted,
            sync_status AS SyncStatus
        FROM files
        WHERE sync_status = 'pending'
        ORDER BY id
        LIMIT @Limit;
        """;

        var files = await connection.QueryAsync<FileMetadata>(sql, new { Limit = limit });

        return files.ToList();
    }

    public async Task MarkFilesAsSyncedAsync(List<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_database.ConnectionString);

        var sql =
        """
        UPDATE files
        SET sync_status = 'synced'
        WHERE id IN @Ids;
        """;

        await connection.ExecuteAsync(sql, new { Ids = ids });
    }

    public async Task<int> CountFilesAsync()
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM files WHERE is_deleted = 0;"
        );
    }

    public async Task<int> CountPendingFilesAsync()
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM files WHERE sync_status = 'pending';"
        );
    }

    public async Task<List<FileMetadata>> GetFilesPendingAutomaticUploadAsync(
        IReadOnlyCollection<string> extensions,
        long afterId,
        int limit)
    {
        if (extensions.Count == 0)
        {
            return [];
        }

        using var connection = new SqliteConnection(_database.ConnectionString);

        const string sql =
        """
        SELECT
            id AS Id,
            device_code AS DeviceCode,
            full_path AS FullPath,
            directory_path AS DirectoryPath,
            file_name AS FileName,
            extension AS Extension,
            size_bytes AS SizeBytes,
            created_at_utc AS CreatedAtUtc,
            updated_at_utc AS UpdatedAtUtc,
            last_seen_at_utc AS LastSeenAtUtc,
            is_deleted AS IsDeleted,
            sync_status AS SyncStatus,
            uploaded_size_bytes AS UploadedSizeBytes,
            uploaded_updated_at_utc AS UploadedUpdatedAtUtc,
            upload_status AS UploadStatus,
            upload_error AS UploadError,
            last_upload_attempt_at_utc AS LastUploadAttemptAtUtc
        FROM files
        WHERE id > @AfterId
          AND is_deleted = 0
          AND lower(extension) IN @Extensions
          AND (
              uploaded_size_bytes IS NULL
              OR uploaded_updated_at_utc IS NULL
              OR uploaded_size_bytes != size_bytes
              OR uploaded_updated_at_utc != updated_at_utc
          )
        ORDER BY id
        LIMIT @Limit;
        """;

        var files = await connection.QueryAsync<FileMetadata>(
            sql,
            new
            {
                AfterId = afterId,
                Extensions = extensions.Select(x => x.ToLowerInvariant()).ToArray(),
                Limit = limit
            }
        );

        return files.ToList();
    }

    public async Task MarkAutomaticUploadStartedAsync(long id)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        await connection.ExecuteAsync(
            """
            UPDATE files
            SET upload_status = 'uploading',
                upload_error = '',
                last_upload_attempt_at_utc = @AttemptedAtUtc
            WHERE id = @Id;
            """,
            new { Id = id, AttemptedAtUtc = DateTime.UtcNow }
        );
    }

    public async Task MarkAutomaticUploadCompletedAsync(FileMetadata file)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        await connection.ExecuteAsync(
            """
            UPDATE files
            SET uploaded_size_bytes = @SizeBytes,
                uploaded_updated_at_utc = @UpdatedAtUtc,
                upload_status = 'completed',
                upload_error = '',
                last_upload_attempt_at_utc = @AttemptedAtUtc
            WHERE id = @Id
              AND size_bytes = @SizeBytes
              AND updated_at_utc = @UpdatedAtUtc;
            """,
            new
            {
                file.Id,
                file.SizeBytes,
                file.UpdatedAtUtc,
                AttemptedAtUtc = DateTime.UtcNow
            }
        );
    }

    public async Task MarkAutomaticUploadFailedAsync(long id, string errorMessage)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        await connection.ExecuteAsync(
            """
            UPDATE files
            SET upload_status = 'failed',
                upload_error = @ErrorMessage,
                last_upload_attempt_at_utc = @AttemptedAtUtc
            WHERE id = @Id;
            """,
            new
            {
                Id = id,
                ErrorMessage = errorMessage,
                AttemptedAtUtc = DateTime.UtcNow
            }
        );
    }

    public async Task MarkFileMissingAsync(long id)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

        await connection.ExecuteAsync(
            """
            UPDATE files
            SET is_deleted = 1,
                sync_status = 'pending',
                upload_status = 'missing',
                upload_error = '',
                last_upload_attempt_at_utc = @AttemptedAtUtc
            WHERE id = @Id;
            """,
            new
            {
                Id = id,
                AttemptedAtUtc = DateTime.UtcNow
            }
        );
    }
}
