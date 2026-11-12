PRAGMA foreign_keys = ON; -- SQLite: must also be set on every new connection in code

-- 1. Courses
CREATE TABLE courses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    description TEXT,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);

-- 2. Uploaded documents
CREATE TABLE documents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id INTEGER NOT NULL,
    filename TEXT NOT NULL,
    file_type TEXT NOT NULL
        CHECK (file_type IN ('pdf', 'pptx', 'docx')),
    file_path TEXT NOT NULL,
    extracted_text TEXT,
    processing_status TEXT DEFAULT 'pending'
        CHECK (processing_status IN (
            'pending', 'processing', 'completed', 'failed'
        )),
    error_message TEXT,          -- set by the Python ingestion pipeline on failure
    uploaded_at TEXT DEFAULT CURRENT_TIMESTAMP,
    deleted_at TEXT,
    FOREIGN KEY (course_id) REFERENCES courses(id)
        ON DELETE CASCADE
);

-- 3. Topics generated from documents
CREATE TABLE topics (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    description TEXT,
    sequence_number INTEGER NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (course_id) REFERENCES courses(id)
        ON DELETE CASCADE,
    UNIQUE(course_id, sequence_number)
);

-- 4. Concepts within topics
-- Prerequisites now live in concept_prerequisites (below), not a JSON column.
CREATE TABLE concepts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    topic_id INTEGER NOT NULL,
    title TEXT NOT NULL,
    learning_objective TEXT,
    sequence_number INTEGER NOT NULL,
    difficulty_level INTEGER DEFAULT 1
        CHECK (difficulty_level BETWEEN 1 AND 3),
    FOREIGN KEY (topic_id) REFERENCES topics(id)
        ON DELETE CASCADE,
    UNIQUE(topic_id, sequence_number)
);

-- 4b. Concept prerequisites (replaces JSON array)
CREATE TABLE concept_prerequisites (
    concept_id INTEGER NOT NULL,
    prerequisite_id INTEGER NOT NULL,
    PRIMARY KEY (concept_id, prerequisite_id),
    CHECK (concept_id <> prerequisite_id),
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
        ON DELETE CASCADE,
    FOREIGN KEY (prerequisite_id) REFERENCES concepts(id)
        ON DELETE CASCADE
);

-- 5. Links concepts to their source material
CREATE TABLE concept_sources (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    concept_id INTEGER NOT NULL,
    document_id INTEGER NOT NULL,
    page_number INTEGER,
    slide_number INTEGER,
    relevant_text TEXT,
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
        ON DELETE CASCADE,
    FOREIGN KEY (document_id) REFERENCES documents(id)
        ON DELETE CASCADE
);

-- 6. Learning sessions
CREATE TABLE sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id INTEGER NOT NULL,
    current_topic_id INTEGER,
    current_concept_id INTEGER,
    current_level INTEGER DEFAULT 1
        CHECK (current_level BETWEEN 1 AND 3),
    status TEXT DEFAULT 'active'
        CHECK (status IN ('active', 'completed', 'paused')),
    started_at TEXT DEFAULT CURRENT_TIMESTAMP,
    ended_at TEXT,
    FOREIGN KEY (course_id) REFERENCES courses(id),
    FOREIGN KEY (current_topic_id) REFERENCES topics(id),
    FOREIGN KEY (current_concept_id) REFERENCES concepts(id)
);

-- 7. Generated teaching content (NEW)
-- One row per explanation shown. Re-teaching creates a new row at an easier level,
-- so level 1 vs level 2 explanations can be compared side by side.
CREATE TABLE lessons (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    concept_id INTEGER NOT NULL,
    session_id INTEGER NOT NULL,
    level INTEGER NOT NULL
        CHECK (level BETWEEN 1 AND 3),
    content TEXT NOT NULL,
    format TEXT DEFAULT 'text'
        CHECK (format IN ('text', 'analogy', 'example', 'steps', 'interactive')),
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
        ON DELETE CASCADE,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
        ON DELETE CASCADE
);

-- 8. Assessments: quizzes, cumulative tests and spaced-review quizzes
CREATE TABLE assessments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    assessment_type TEXT NOT NULL
        CHECK (assessment_type IN ('concept_quiz', 'cumulative', 'review')),
    title TEXT,
    total_questions INTEGER DEFAULT 0,
    score REAL,
    completed_at TEXT,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
        ON DELETE CASCADE
);

-- 9. Questions belonging to assessments
CREATE TABLE assessment_questions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    assessment_id INTEGER NOT NULL,
    concept_id INTEGER NOT NULL,
    question TEXT NOT NULL,
    question_type TEXT DEFAULT 'short_answer'
        CHECK (question_type IN ('mcq', 'short_answer', 'true_false')),
    options TEXT, -- JSON array for MCQs
    correct_answer TEXT,
    explanation TEXT,
    difficulty_level INTEGER DEFAULT 1
        CHECK (difficulty_level BETWEEN 1 AND 3),
    is_review INTEGER DEFAULT 0
        CHECK (is_review IN (0, 1)), -- 1 = injected from an earlier section
    FOREIGN KEY (assessment_id) REFERENCES assessments(id)
        ON DELETE CASCADE,
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
);

-- 10. Student responses to assessment questions
-- score is the real signal (0 to 1, LLM-graded for short answers).
-- is_correct is derived: score >= 0.7 (document this threshold in the report).
CREATE TABLE assessment_responses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    question_id INTEGER NOT NULL,
    student_answer TEXT,
    score REAL
        CHECK (score IS NULL OR score BETWEEN 0 AND 1),
    feedback TEXT,
    is_correct INTEGER
        CHECK (is_correct IS NULL OR is_correct IN (0, 1)),
    answered_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (question_id) REFERENCES assessment_questions(id)
        ON DELETE CASCADE
);

-- 11. Conversation history
CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    concept_id INTEGER,
    lesson_id INTEGER,
    assessment_id INTEGER,
    role TEXT NOT NULL
        CHECK (role IN ('student', 'tutor', 'system')),
    content TEXT NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
        ON DELETE CASCADE,
    FOREIGN KEY (concept_id) REFERENCES concepts(id),
    FOREIGN KEY (lesson_id) REFERENCES lessons(id)
        ON DELETE SET NULL,
    FOREIGN KEY (assessment_id) REFERENCES assessments(id)
        ON DELETE SET NULL
);

-- 12. Student progress for each concept
-- Single-user by design: concept_id is UNIQUE with no user link.
-- For multi-user support, add user_id and make UNIQUE(user_id, concept_id).
-- Spaced review: next_review_at is set from review_count using a simple
-- doubling interval (e.g. 1, 2, 4, 8 days). Implemented in app code.
CREATE TABLE concept_progress (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    concept_id INTEGER NOT NULL UNIQUE,
    mastery_score REAL DEFAULT 0
        CHECK (mastery_score BETWEEN 0 AND 100),
    current_level INTEGER DEFAULT 1
        CHECK (current_level BETWEEN 1 AND 3),
    attempts INTEGER DEFAULT 0,
    last_assessed_at TEXT,
    next_review_at TEXT,
    review_count INTEGER DEFAULT 0,
    status TEXT DEFAULT 'not_started'
        CHECK (status IN (
            'not_started', 'learning', 'needs_revision', 'mastered'
        )),
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
        ON DELETE CASCADE
);

-- 13. Records adaptive teaching decisions
CREATE TABLE adaptive_decisions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    concept_id INTEGER NOT NULL,
    understanding_score REAL,
    previous_level INTEGER,
    new_level INTEGER,
    action TEXT NOT NULL
        CHECK (action IN ('advance', 'reteach', 'review', 'hold')),
    reason TEXT,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
        ON DELETE CASCADE,
    FOREIGN KEY (concept_id) REFERENCES concepts(id)
);

-- 14. Verbatim text passages extracted from uploaded documents (C# ingestion pipeline)
CREATE TABLE chunks (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id  INTEGER NOT NULL,
    chunk_index  INTEGER NOT NULL,
    text         TEXT    NOT NULL,
    page_start   INTEGER,
    page_end     INTEGER,
    heading      TEXT,
    embedding    TEXT,   -- JSON float array (768-dim, text-embedding-004)
    UNIQUE (document_id, chunk_index),
    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
);

-- 15. Learning sections derived from chunks (Python ingestion pipeline)
CREATE TABLE document_sections (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id   INTEGER NOT NULL,
    section_index INTEGER NOT NULL,
    title         TEXT    NOT NULL,
    summary       TEXT,
    key_concepts  TEXT,   -- JSON array of concept name strings
    difficulty    INTEGER DEFAULT 1
                  CHECK (difficulty BETWEEN 1 AND 3),
    chunk_indexes TEXT,   -- JSON array of 0-based chunk indexes
    UNIQUE (document_id, section_index),
    FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
);

-- Indexes
CREATE INDEX idx_documents_course
    ON documents(course_id);

CREATE INDEX idx_topics_course
    ON topics(course_id, sequence_number);

CREATE INDEX idx_concepts_topic
    ON concepts(topic_id, sequence_number);

CREATE INDEX idx_concept_sources_concept
    ON concept_sources(concept_id);

CREATE INDEX idx_lessons_concept
    ON lessons(concept_id, level);

CREATE INDEX idx_messages_session
    ON messages(session_id, created_at);

CREATE INDEX idx_assessments_session
    ON assessments(session_id);

CREATE INDEX idx_questions_assessment
    ON assessment_questions(assessment_id);

CREATE INDEX idx_questions_concept
    ON assessment_questions(concept_id);

CREATE INDEX idx_responses_question
    ON assessment_responses(question_id);

CREATE INDEX idx_progress_review
    ON concept_progress(status, next_review_at);

CREATE INDEX idx_decisions_concept
    ON adaptive_decisions(concept_id, created_at);

CREATE INDEX idx_chunks_document
    ON chunks(document_id, chunk_index);

CREATE INDEX idx_sections_document
    ON document_sections(document_id, section_index);
