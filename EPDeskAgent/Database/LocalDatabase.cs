using Microsoft.Data.Sqlite;

namespace EPDeskAgent.Database
{
    public class LocalDatabase
    {
        private readonly string _dbPath;

        public LocalDatabase(IConfiguration configuration)
        {
            _dbPath = configuration["Agent:DatabasePath"]
                      ?? "C:\\ProgramData\\EPDeskAgent\\metadata.db";
        }

        public string ConnectionString => $"Data Source={_dbPath}";

        public void Initialize()
        {
            var folder = Path.GetDirectoryName(_dbPath);

            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var command = connection.CreateCommand();

            command.CommandText =
            """
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            device_code TEXT NOT NULL,
            full_path TEXT NOT NULL UNIQUE,
            directory_path TEXT NOT NULL,
            file_name TEXT NOT NULL,
            extension TEXT,
            size_bytes INTEGER NOT NULL,
            created_at_utc TEXT,
            updated_at_utc TEXT,
            last_seen_at_utc TEXT,
            is_deleted INTEGER DEFAULT 0,
            sync_status TEXT DEFAULT 'pending'
        );

        CREATE INDEX IF NOT EXISTS idx_files_name ON files(file_name);
        CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
        CREATE INDEX IF NOT EXISTS idx_files_sync_status ON files(sync_status);
        """;

            command.ExecuteNonQuery();
        }
    }
}