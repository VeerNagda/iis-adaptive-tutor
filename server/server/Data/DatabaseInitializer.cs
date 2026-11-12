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

        // ══ Adaptive teaching ("Start Learning") ══════════════════════════════
        // The existing topics and sessions tables are reused with extra columns.
        // The older lessons/assessments/messages tables from schema.sql are left
        // untouched and unused: their CHECK constraints (levels 1-3, concept_id
        // NOT NULL) do not fit the 1-5 difficulty model, and SQLite cannot alter
        // a CHECK constraint without rebuilding the table.

        // ── topics: where the content comes from + the learner's state ────────
        // chunk_ids        JSON array of chunks.id that this topic is taught from
        // difficulty_level 1 (easiest) .. 5 (hardest); starts at 3, remembered across sessions
        // status           new | learning | completed | weak
        if (!ColExists("topics", "chunk_ids"))
            Exec("ALTER TABLE topics ADD COLUMN chunk_ids TEXT");
        if (!ColExists("topics", "difficulty_level"))
            Exec("ALTER TABLE topics ADD COLUMN difficulty_level INTEGER NOT NULL DEFAULT 3");
        if (!ColExists("topics", "status"))
            Exec("ALTER TABLE topics ADD COLUMN status TEXT NOT NULL DEFAULT 'new'");

        // ── sessions: position in the lesson loop (one session = one topic) ───
        // phase            lesson | quiz | checkpoint | review | retest | done
        // step_index       0-based lesson step within the topic
        // attempt          1 = first explanation of this step, 2+ = re-teach
        // since_checkpoint lessons completed since the last checkpoint test
        // checkpoint_every 3 or 4 (picked at random after each checkpoint)
        // checkpoint_from  first step covered by the next checkpoint
        // review_round     0 = not in review, 1-2 = weak-topic review round
        // reason           why the current content is shown (transparency label)
        if (!ColExists("sessions", "phase"))
            Exec("ALTER TABLE sessions ADD COLUMN phase TEXT NOT NULL DEFAULT 'lesson'");
        if (!ColExists("sessions", "step_index"))
            Exec("ALTER TABLE sessions ADD COLUMN step_index INTEGER NOT NULL DEFAULT 0");
        if (!ColExists("sessions", "attempt"))
            Exec("ALTER TABLE sessions ADD COLUMN attempt INTEGER NOT NULL DEFAULT 1");
        if (!ColExists("sessions", "since_checkpoint"))
            Exec("ALTER TABLE sessions ADD COLUMN since_checkpoint INTEGER NOT NULL DEFAULT 0");
        if (!ColExists("sessions", "checkpoint_every"))
            Exec("ALTER TABLE sessions ADD COLUMN checkpoint_every INTEGER NOT NULL DEFAULT 3");
        if (!ColExists("sessions", "checkpoint_from"))
            Exec("ALTER TABLE sessions ADD COLUMN checkpoint_from INTEGER NOT NULL DEFAULT 0");
        if (!ColExists("sessions", "review_round"))
            Exec("ALTER TABLE sessions ADD COLUMN review_round INTEGER NOT NULL DEFAULT 0");
        if (!ColExists("sessions", "reason"))
            Exec("ALTER TABLE sessions ADD COLUMN reason TEXT");

        // ── learn_lessons: every lesson text shown to the learner ─────────────
        // kind = 'lesson' (normal step) or 'review' (weak-topic re-teach after a checkpoint)
        if (!TableExists("learn_lessons"))
            Exec("""
                CREATE TABLE learn_lessons (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id  INTEGER NOT NULL,
                    topic_id    INTEGER NOT NULL,
                    kind        TEXT    NOT NULL DEFAULT 'lesson',
                    step_index  INTEGER NOT NULL,
                    attempt     INTEGER NOT NULL,
                    level       INTEGER NOT NULL,
                    text        TEXT    NOT NULL,
                    example     TEXT,
                    insight     TEXT,
                    reason      TEXT,
                    created_at  TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE,
                    FOREIGN KEY (topic_id)   REFERENCES topics(id)   ON DELETE CASCADE
                )
                """);

        // ── learn_quizzes: one row per quiz taken ─────────────────────────────
        // kind = 'lesson' (after a lesson), 'checkpoint' (mixed test), 'retest' (after a review)
        // correct/passed stay NULL until the learner submits answers.
        if (!TableExists("learn_quizzes"))
            Exec("""
                CREATE TABLE learn_quizzes (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id   INTEGER NOT NULL,
                    topic_id     INTEGER NOT NULL,
                    lesson_id    INTEGER,
                    kind         TEXT    NOT NULL,
                    total        INTEGER NOT NULL,
                    correct      INTEGER,
                    passed       INTEGER,
                    simulated    INTEGER NOT NULL DEFAULT 0,
                    created_at   TEXT DEFAULT CURRENT_TIMESTAMP,
                    completed_at TEXT,
                    FOREIGN KEY (session_id) REFERENCES sessions(id)      ON DELETE CASCADE,
                    FOREIGN KEY (topic_id)   REFERENCES topics(id)        ON DELETE CASCADE,
                    FOREIGN KEY (lesson_id)  REFERENCES learn_lessons(id) ON DELETE CASCADE
                )
                """);

        // ── learn_questions: quiz questions plus the learner's answer ─────────
        // topic_id is per question because a checkpoint mixes several topics.
        if (!TableExists("learn_questions"))
            Exec("""
                CREATE TABLE learn_questions (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    quiz_id       INTEGER NOT NULL,
                    topic_id      INTEGER NOT NULL,
                    text          TEXT    NOT NULL,
                    options       TEXT    NOT NULL,
                    correct_index INTEGER NOT NULL,
                    explanation   TEXT,
                    label         TEXT,
                    user_answer   INTEGER,
                    is_correct    INTEGER,
                    FOREIGN KEY (quiz_id)  REFERENCES learn_quizzes(id) ON DELETE CASCADE,
                    FOREIGN KEY (topic_id) REFERENCES topics(id)        ON DELETE CASCADE
                )
                """);

        // ── difficulty_history: every level change, with the reason ───────────
        if (!TableExists("difficulty_history"))
            Exec("""
                CREATE TABLE difficulty_history (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    topic_id   INTEGER NOT NULL,
                    session_id INTEGER,
                    old_level  INTEGER NOT NULL,
                    new_level  INTEGER NOT NULL,
                    reason     TEXT    NOT NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (topic_id)   REFERENCES topics(id)   ON DELETE CASCADE,
                    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE SET NULL
                )
                """);

        // ── checkpoint_results: score per topic for each checkpoint / re-test ─
        if (!TableExists("checkpoint_results"))
            Exec("""
                CREATE TABLE checkpoint_results (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    quiz_id    INTEGER NOT NULL,
                    session_id INTEGER NOT NULL,
                    topic_id   INTEGER NOT NULL,
                    correct    INTEGER NOT NULL,
                    total      INTEGER NOT NULL,
                    score_pct  INTEGER NOT NULL,
                    is_weak    INTEGER NOT NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (quiz_id)    REFERENCES learn_quizzes(id) ON DELETE CASCADE,
                    FOREIGN KEY (session_id) REFERENCES sessions(id)      ON DELETE CASCADE,
                    FOREIGN KEY (topic_id)   REFERENCES topics(id)        ON DELETE CASCADE
                )
                """);

        // ── indexes (IF NOT EXISTS is safe for indexes) ───────────────────────
        Exec("CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id, chunk_index)");
        Exec("CREATE INDEX IF NOT EXISTS idx_sections_document ON document_sections(document_id, section_index)");
        Exec("CREATE INDEX IF NOT EXISTS idx_learn_lessons_session ON learn_lessons(session_id, kind, step_index, attempt)");
        Exec("CREATE INDEX IF NOT EXISTS idx_learn_quizzes_session ON learn_quizzes(session_id)");
        Exec("CREATE INDEX IF NOT EXISTS idx_learn_questions_quiz ON learn_questions(quiz_id)");
    }
}
