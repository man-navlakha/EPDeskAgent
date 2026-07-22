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
            ,uploaded_size_bytes INTEGER NULL
            ,uploaded_updated_at_utc TEXT NULL
            ,upload_status TEXT DEFAULT 'pending'
            ,upload_error TEXT DEFAULT ''
            ,last_upload_attempt_at_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_files_name ON files(file_name);
        CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
        CREATE INDEX IF NOT EXISTS idx_files_sync_status ON files(sync_status);
        """;

            command.ExecuteNonQuery();

            EnsureColumn(connection, "files", "uploaded_size_bytes", "INTEGER NULL");
            EnsureColumn(connection, "files", "uploaded_updated_at_utc", "TEXT NULL");
            EnsureColumn(connection, "files", "upload_status", "TEXT DEFAULT 'pending'");
            EnsureColumn(connection, "files", "upload_error", "TEXT DEFAULT ''");
            EnsureColumn(connection, "files", "last_upload_attempt_at_utc", "TEXT NULL");

            using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_files_upload_status ON files(upload_status);";
            indexCommand.ExecuteNonQuery();
        }

        private static void EnsureColumn(
            SqliteConnection connection,
            string tableName,
            string columnName,
            string definition)
        {
            using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = $"PRAGMA table_info({tableName});";

            using var reader = schemaCommand.ExecuteReader();
            var exists = false;

            while (reader.Read())
            {
                if (string.Equals(
                        reader.GetString(1),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            reader.Close();

            if (exists)
            {
                return;
            }

            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText =
                $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
            alterCommand.ExecuteNonQuery();
        }
    }
}
