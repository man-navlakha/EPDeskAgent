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

    public async Task UpsertFileAsync(FileMetadata file)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);

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
            directory_path = excluded.directory_path,
            file_name = excluded.file_name,
            extension = excluded.extension,
            size_bytes = excluded.size_bytes,
            created_at_utc = excluded.created_at_utc,
            updated_at_utc = excluded.updated_at_utc,
            last_seen_at_utc = excluded.last_seen_at_utc,
            is_deleted = 0,
            sync_status = 'pending';
        """;

        await connection.ExecuteAsync(sql, file);
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
}