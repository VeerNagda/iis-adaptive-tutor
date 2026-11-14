using Microsoft.Data.Sqlite;

namespace server.Data;

/// <summary>
/// Prepares the SQLite database on startup.
///
/// Database/schema.sql is the single place where the tables are defined. Every
/// statement in it uses IF NOT EXISTS, so running it creates the whole schema on
/// a fresh install and does nothing on a database that is already complete.
/// </summary>
public static class DatabaseInitializer
{
    public static void Initialize(IDbConnectionFactory factory, string schemaPath)
    {
        var statements = File.ReadAllText(schemaPath)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        using var connection = factory.CreateConnection();

        // Only does something for a development database made before the schema
        // was cleaned up; a fresh install skips it.
        UpgradeOldDatabase(connection, statements);

        using (var tx = connection.BeginTransaction())
        {
            foreach (var sql in statements) Exec(connection, sql);
            tx.Commit();
        }

        AddMissingColumns(connection);
    }

    /// <summary>
    /// One-time clean-up of a database created by the first version of the
    /// prototype. It removes the tables that were designed but never used, and
    /// rebuilds sessions (which pointed at one of them). Courses, documents,
    /// ingested chunks and learning history are kept.
    /// </summary>
    private static void UpgradeOldDatabase(SqliteConnection connection, List<string> statements)
    {
        // The unused 'concepts' table is the sign of the old layout.
        if (!TableExists(connection, "concepts")) return;

        // Foreign keys must be off while tables are dropped and rebuilt. The
        // setting cannot change inside a transaction, so it wraps the transaction.
        Exec(connection, "PRAGMA foreign_keys = OFF");
        using (var tx = connection.BeginTransaction())
        {
            string[] unusedTables =
            [
                "messages", "assessment_responses", "assessment_questions", "assessments",
                "adaptive_decisions", "concept_progress", "concept_sources",
                "concept_prerequisites", "lessons", "concepts",
            ];
            foreach (var table in unusedTables)
                Exec(connection, $"DROP TABLE IF EXISTS {table}");

            // SQLite cannot drop a foreign key or change a CHECK, so the old
            // sessions table is replaced: create the new one from schema.sql under a
            // temporary name, copy the learning sessions across, then swap the names.
            var createSessions = statements.First(s => s.Contains("CREATE TABLE IF NOT EXISTS sessions"));
            Exec(connection, createSessions.Replace("IF NOT EXISTS sessions", "sessions_new"));

            if (ColumnExists(connection, "sessions", "phase"))
                Exec(connection, """
                    INSERT INTO sessions_new
                        (id, course_id, current_topic_id, status, phase, step_index, attempt, since_checkpoint,
                         checkpoint_every, checkpoint_from, review_round, reason, started_at, ended_at)
                    SELECT id, course_id, current_topic_id,
                           CASE WHEN status = 'completed' THEN 'completed' ELSE 'active' END,
                           phase, step_index, attempt, since_checkpoint,
                           checkpoint_every, checkpoint_from, review_round, reason, started_at, ended_at
                    FROM sessions
                    WHERE current_topic_id IS NOT NULL
                    """);

            Exec(connection, "DROP TABLE sessions");
            Exec(connection, "ALTER TABLE sessions_new RENAME TO sessions");
            tx.Commit();
        }
        Exec(connection, "PRAGMA foreign_keys = ON");
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS never changes a table that already exists, so
    /// columns added to schema.sql later are also listed here for existing databases.
    /// </summary>
    private static void AddMissingColumns(SqliteConnection connection)
    {
        AddColumn(connection, "topics", "chunk_ids", "TEXT");
        AddColumn(connection, "topics", "difficulty_level", "INTEGER NOT NULL DEFAULT 3");
        AddColumn(connection, "topics", "status", "TEXT NOT NULL DEFAULT 'new'");
        AddColumn(connection, "learn_questions", "option_feedback", "TEXT");
        AddColumn(connection, "courses", "topic_test_every", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "topics", "awaiting_test", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "sessions", "kind", "TEXT NOT NULL DEFAULT 'topic'");
        AddColumn(connection, "documents", "ingest_report", "TEXT");
        AddColumn(connection, "learn_questions", "source_quote", "TEXT");
        AddColumn(connection, "difficulty_history", "question_count", "INTEGER");
        AddColumn(connection, "difficulty_history", "choice", "TEXT");
        AddColumn(connection, "courses", "adaptation_paused", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "learn_questions", "slip", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "learn_questions", "flagged", "INTEGER NOT NULL DEFAULT 0");

        // Quizzes finished before the two-step flow existed count as submitted too.
        if (AddColumn(connection, "learn_quizzes", "submitted_at", "TEXT"))
            Exec(connection, "UPDATE learn_quizzes SET submitted_at = completed_at WHERE completed_at IS NOT NULL");

        // Documents whose topics already exist must not be extracted a second time.
        if (AddColumn(connection, "documents", "topics_extracted", "INTEGER NOT NULL DEFAULT 0"))
            Exec(connection, """
                UPDATE documents SET topics_extracted = 1
                WHERE processing_status = 'completed' AND course_id IN (SELECT course_id FROM topics)
                """);
    }

    /// <summary>Adds a column if the table does not have it yet. Returns true if it was added.</summary>
    private static bool AddColumn(SqliteConnection connection, string table, string column, string definition)
    {
        if (ColumnExists(connection, table, column)) return false;
        Exec(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        return true;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $name";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$name", column);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
