-- Complete database schema for the adaptive tutor.
--
-- DatabaseInitializer runs this file on every start. Every statement uses
-- IF NOT EXISTS, so on a fresh install it creates everything and on an existing
-- database it changes nothing.
--
-- Editing note: the file is split into statements on the semicolon character,
-- so never write a semicolon inside a comment.

-- ── Course material ──────────────────────────────────────────────────────────

-- 1. Courses
CREATE TABLE IF NOT EXISTS courses (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT NOT NULL,
    description TEXT,
    created_at  TEXT DEFAULT CURRENT_TIMESTAMP
);

-- 2. Uploaded documents
CREATE TABLE IF NOT EXISTS documents (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id         INTEGER NOT NULL,
    filename          TEXT NOT NULL,
    file_type         TEXT NOT NULL CHECK (file_type IN ('pdf', 'pptx', 'docx')),
    file_path         TEXT NOT NULL,
    extracted_text    TEXT,
    processing_status TEXT DEFAULT 'pending'
        CHECK (processing_status IN ('pending', 'processing', 'completed', 'failed')),
    error_message     TEXT,
    uploaded_at       TEXT DEFAULT CURRENT_TIMESTAMP,
    deleted_at        TEXT,
    FOREIGN KEY (course_id) REFERENCES courses(id) ON DELETE CASCADE
);

-- 3. Text passages extracted from a document (ingestion pipeline)
CREATE TABLE IF NOT EXISTS chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id INTEGER NOT NULL,
    chunk_index INTEGER NOT NULL,
    text        TEXT    NOT NULL,
    page_start  INTEGER,
    page_end    INTEGER,
    heading     TEXT,
    embedding   TEXT,   -- JSON float array
    UNIQUE (document_id, chunk_index),
    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
);

-- 4. Learning sections of a document, each grouping some chunks (ingestion pipeline)
CREATE TABLE IF NOT EXISTS document_sections (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id   INTEGER NOT NULL,
    section_index INTEGER NOT NULL,
    title         TEXT    NOT NULL,
    summary       TEXT,
    key_concepts  TEXT,   -- JSON array of concept names
    difficulty    INTEGER DEFAULT 1 CHECK (difficulty BETWEEN 1 AND 3),
    chunk_indexes TEXT,   -- JSON array of 0-based chunk indexes
    UNIQUE (document_id, section_index),
    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
);

-- ── Adaptive teaching ("Start Learning") ─────────────────────────────────────

-- 5. Topics extracted from the sections by the LLM, plus the learner's state
--    chunk_ids         JSON array of chunks.id the topic is taught from
--    difficulty_level  1 (easiest) to 5 (hardest), starts at 3, remembered across sessions
--    status            new, learning, completed or weak
CREATE TABLE IF NOT EXISTS topics (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id        INTEGER NOT NULL,
    title            TEXT NOT NULL,
    description      TEXT,
    sequence_number  INTEGER NOT NULL,
    chunk_ids        TEXT,
    difficulty_level INTEGER NOT NULL DEFAULT 3 CHECK (difficulty_level BETWEEN 1 AND 5),
    status           TEXT NOT NULL DEFAULT 'new'
        CHECK (status IN ('new', 'learning', 'completed', 'weak')),
    created_at       TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (course_id) REFERENCES courses(id) ON DELETE CASCADE,
    UNIQUE (course_id, sequence_number)
);

-- 6. Learning sessions. One session teaches one topic, and the row stores the
--    position in the lesson loop so a refresh resumes at the same place.
--    phase             lesson, quiz, checkpoint, review, retest or done
--    step_index        0-based lesson step within the topic
--    attempt           1 for the first explanation of a step, 2 or more for a re-teach
--    since_checkpoint  lessons completed since the last checkpoint test
--    checkpoint_every  3 or 4, picked at random after each checkpoint
--    checkpoint_from   first step covered by the next checkpoint
--    review_round      0 when not in review, 1 or 2 during a weak-topic review
--    reason            why the current content is shown (transparency label)
CREATE TABLE IF NOT EXISTS sessions (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id        INTEGER NOT NULL,
    current_topic_id INTEGER,
    status           TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed')),
    phase            TEXT NOT NULL DEFAULT 'lesson',
    step_index       INTEGER NOT NULL DEFAULT 0,
    attempt          INTEGER NOT NULL DEFAULT 1,
    since_checkpoint INTEGER NOT NULL DEFAULT 0,
    checkpoint_every INTEGER NOT NULL DEFAULT 3,
    checkpoint_from  INTEGER NOT NULL DEFAULT 0,
    review_round     INTEGER NOT NULL DEFAULT 0,
    reason           TEXT,
    started_at       TEXT DEFAULT CURRENT_TIMESTAMP,
    ended_at         TEXT,
    FOREIGN KEY (course_id)        REFERENCES courses(id) ON DELETE CASCADE,
    FOREIGN KEY (current_topic_id) REFERENCES topics(id)  ON DELETE CASCADE
);

-- 7. Every lesson text shown to the learner
--    kind is 'lesson' (normal step) or 'review' (weak-topic re-teach after a checkpoint)
CREATE TABLE IF NOT EXISTS learn_lessons (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    topic_id   INTEGER NOT NULL,
    kind       TEXT    NOT NULL DEFAULT 'lesson',
    step_index INTEGER NOT NULL,
    attempt    INTEGER NOT NULL,
    level      INTEGER NOT NULL,
    text       TEXT    NOT NULL,
    example    TEXT,
    insight    TEXT,
    reason     TEXT,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE,
    FOREIGN KEY (topic_id)   REFERENCES topics(id)   ON DELETE CASCADE
);

-- 8. One row per quiz
--    kind is 'lesson' (after a lesson), 'checkpoint' (mixed test) or 'retest' (after a review)
--    correct and passed stay NULL until the learner submits answers
CREATE TABLE IF NOT EXISTS learn_quizzes (
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
);

-- 9. Quiz questions plus the learner's answer
--    topic_id is per question because a test can mix several topics
CREATE TABLE IF NOT EXISTS learn_questions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    quiz_id       INTEGER NOT NULL,
    topic_id      INTEGER NOT NULL,
    text          TEXT    NOT NULL,
    options       TEXT    NOT NULL,   -- JSON array of option texts
    correct_index INTEGER NOT NULL,   -- 0-based position of the correct option
    explanation   TEXT,               -- why the correct option is right
    option_feedback TEXT,             -- JSON array, one sentence per option: why it is right or wrong
    label         TEXT,               -- for example "Refresher from: Topic X"
    user_answer   INTEGER,
    is_correct    INTEGER,
    FOREIGN KEY (quiz_id)  REFERENCES learn_quizzes(id) ON DELETE CASCADE,
    FOREIGN KEY (topic_id) REFERENCES topics(id)        ON DELETE CASCADE
);

-- 10. Every change of a topic's difficulty level, with the reason
CREATE TABLE IF NOT EXISTS difficulty_history (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    topic_id   INTEGER NOT NULL,
    session_id INTEGER,
    old_level  INTEGER NOT NULL,
    new_level  INTEGER NOT NULL,
    reason     TEXT    NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (topic_id)   REFERENCES topics(id)   ON DELETE CASCADE,
    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE SET NULL
);

-- 11. Score per topic for each checkpoint or re-test (weak means below 60%)
CREATE TABLE IF NOT EXISTS checkpoint_results (
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
);

-- ── Indexes ──────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_documents_course       ON documents(course_id);
CREATE INDEX IF NOT EXISTS idx_chunks_document        ON chunks(document_id, chunk_index);
CREATE INDEX IF NOT EXISTS idx_sections_document      ON document_sections(document_id, section_index);
CREATE INDEX IF NOT EXISTS idx_topics_course          ON topics(course_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_learn_lessons_session  ON learn_lessons(session_id, kind, step_index, attempt);
CREATE INDEX IF NOT EXISTS idx_learn_quizzes_session  ON learn_quizzes(session_id);
CREATE INDEX IF NOT EXISTS idx_learn_questions_quiz   ON learn_questions(quiz_id)
