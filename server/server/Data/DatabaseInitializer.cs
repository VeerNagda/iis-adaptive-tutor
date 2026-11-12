namespace server.Data;

public static class DatabaseInitializer
{
    public static void Initialize(IDbConnectionFactory factory, string schemaPath)
    {
        using var connection = factory.CreateConnection();

        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='courses';";
        var schemaExists = (long)(checkCmd.ExecuteScalar() ?? 0L) > 0;

        if (!schemaExists)
        {
            var ddl = File.ReadAllText(schemaPath);
            var statements = ddl
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !s.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) && s.Length > 0);

            using var tx = connection.BeginTransaction();
            foreach (var sql in statements)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        RunMigrations(connection);
    }

    private static void RunMigrations(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        // Helper: returns true if column exists in a table.
        bool ColExists(string table, string col)
        {
            using var c = connection.CreateCommand();
            c.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{col}'";
            return (long)(c.ExecuteScalar() ?? 0L) > 0;
        }

        // Helper: returns true if a table exists.
        bool TableExists(string table)
        {
            using var c = connection.CreateCommand();
            c.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            return (long)(c.ExecuteScalar() ?? 0L) > 0;
        }

        void Exec(string sql)
        {
            using var c = connection.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        // ── documents: soft-delete and error columns ──────────────────────────
        if (!ColExists("documents", "deleted_at"))
            Exec("ALTER TABLE documents ADD COLUMN deleted_at TEXT");

        if (!ColExists("documents", "error_message"))
            Exec("ALTER TABLE documents ADD COLUMN error_message TEXT");

        // ── chunks (created by schema.sql on fresh installs; migrated here for
        //    databases that existed before the ingestion pipeline was added) ────
        if (!TableExists("chunks"))
            Exec("""
                CREATE TABLE chunks (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    document_id  INTEGER NOT NULL,
                    chunk_index  INTEGER NOT NULL,
                    text         TEXT    NOT NULL,
                    page_start   INTEGER,
                    page_end     INTEGER,
                    heading      TEXT,
                    embedding    TEXT,
                    UNIQUE (document_id, chunk_index),
                    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
                )
                """);

        // embedding column added after initial deploy of chunks table
        if (TableExists("chunks") && !ColExists("chunks", "embedding"))
            Exec("ALTER TABLE chunks ADD COLUMN embedding TEXT");

        // ── document_sections ─────────────────────────────────────────────────
        if (!TableExists("document_sections"))
            Exec("""
                CREATE TABLE document_sections (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    document_id   INTEGER NOT NULL,
                    section_index INTEGER NOT NULL,
                    title         TEXT    NOT NULL,
                    summary       TEXT,
                    key_concepts  TEXT,
                    difficulty    INTEGER DEFAULT 1
                                  CHECK (difficulty BETWEEN 1 AND 3),
                    chunk_indexes TEXT,
                    UNIQUE (document_id, section_index),
                    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
                )
                """);

        // ── indexes (IF NOT EXISTS is safe for indexes) ───────────────────────
        Exec("CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id, chunk_index)");
        Exec("CREATE INDEX IF NOT EXISTS idx_sections_document ON document_sections(document_id, section_index)");
    }
}
