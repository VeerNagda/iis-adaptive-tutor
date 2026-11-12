using System.Text.Json;
using Microsoft.Data.Sqlite;
using server.Data;
using server.LLM;
using server.Models;

namespace server.Services;

/// <summary>
/// The adaptive teaching feature ("Start Learning").
///
/// Division of work:
///   - The LLM only WRITES content (topics, lessons, quiz questions) as JSON.
///   - Everything that DECIDES something (scoring, pass/fail, difficulty level,
///     when to run a checkpoint, which topics are weak) is plain C# in this class,
///     so each decision can be explained and is the same on every run.
/// </summary>
public sealed class LearningService(IDbConnectionFactory db, ILlmProvider llm, ILogger<LearningService> log)
{
    public bool MockMode => llm.IsMock;

    // The page may ask for topics twice at once (React dev mode does this). The
    // lock makes the second request wait and then reuse the cached topics
    // instead of paying for a second LLM call.
    private static readonly SemaphoreSlim LlmLock = new(1, 1);

    // ══ Topics ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the course's topics. The first call extracts them with the LLM
    /// from the ingested sections and caches them in the topics table; later
    /// calls only read the table.
    /// </summary>
    public async Task<TopicListResponse> GetTopicsAsync(int courseId, CancellationToken ct)
    {
        await LlmLock.WaitAsync(ct);
        try
        {
            using var conn = db.CreateConnection();
            var sections = LoadSections(conn, courseId);
            var topics = LoadTopics(conn, courseId);

            if (topics.Count == 0 && sections.Count > 0)
            {
                log.LogInformation("Extracting topics for course {Id} from {N} sections", courseId, sections.Count);

                var sectionList = string.Join("\n",
                    sections.Select(s => $"[S{s.Id}] {s.Title} — {s.Summary}"));
                var reply = await AskAsync<LlmTopicList>(Prompts.Topics(sectionList), ct);

                SaveTopics(conn, courseId, reply.Topics, sections);
                topics = LoadTopics(conn, courseId);
                if (topics.Count == 0)
                    throw new LlmException("The AI returned no usable topics. Please retry.");
            }

            return new TopicListResponse { MockMode = MockMode, HasContent = sections.Count > 0, Topics = topics };
        }
        finally
        {
            LlmLock.Release();
        }
    }

    /// <summary>
    /// Starts a learning session for a topic, or resumes the unfinished one.
    /// Returns the session id, or null if the topic does not belong to the course.
    /// </summary>
    public int? StartSession(int courseId, int topicId)
    {
        using var conn = db.CreateConnection();
        var topic = LoadTopics(conn, courseId).FirstOrDefault(t => t.Id == topicId);
        if (topic is null) return null;

        var existing = Scalar(conn,
            "SELECT id FROM sessions WHERE current_topic_id = $t AND status = 'active' ORDER BY id DESC LIMIT 1",
            ("$t", topicId));
        if (existing is not null) return Convert.ToInt32(existing);

        var id = Convert.ToInt32(Scalar(conn,
            "INSERT INTO sessions (course_id, current_topic_id) VALUES ($c, $t) RETURNING id",
            ("$c", courseId), ("$t", topicId)));

        // A weak topic stays 'weak' until a re-test is passed.
        if (topic.Status != "weak")
            Exec(conn, "UPDATE topics SET status = 'learning' WHERE id = $t", ("$t", topicId));

        return id;
    }

    private sealed record SectionRow(int Id, int DocumentId, string Title, string Summary, List<int> ChunkIndexes);

    /// <summary>Sections of the course's successfully ingested, non-deleted documents.</summary>
    private static List<SectionRow> LoadSections(SqliteConnection conn, int courseId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.document_id, s.title, s.summary, s.chunk_indexes
            FROM document_sections s
            JOIN documents d ON d.id = s.document_id
            WHERE d.course_id = $c AND d.deleted_at IS NULL AND d.processing_status = 'completed'
            ORDER BY s.document_id, s.section_index
            """;
        cmd.Parameters.AddWithValue("$c", courseId);

        var rows = new List<SectionRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var indexes = r.IsDBNull(4) ? [] : JsonSerializer.Deserialize<List<int>>(r.GetString(4)) ?? [];
            rows.Add(new SectionRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3), indexes));
        }
        return rows;
    }

    private static List<Topic> LoadTopics(SqliteConnection conn, int courseId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, course_id, title, description, sequence_number, difficulty_level, status, chunk_ids
            FROM topics WHERE course_id = $c ORDER BY sequence_number
            """;
        cmd.Parameters.AddWithValue("$c", courseId);

        var topics = new List<Topic>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            topics.Add(new Topic
            {
                Id = r.GetInt32(0),
                CourseId = r.GetInt32(1),
                Title = r.GetString(2),
                Summary = r.IsDBNull(3) ? null : r.GetString(3),
                SequenceNumber = r.GetInt32(4),
                DifficultyLevel = r.GetInt32(5),
                Status = r.GetString(6),
                ChunkIds = r.IsDBNull(7) ? [] : JsonSerializer.Deserialize<List<int>>(r.GetString(7)) ?? [],
            });
        }
        return topics;
    }

    /// <summary>
    /// Stores the LLM's topics. The LLM only names section ids; here they are
    /// checked against the real sections and translated into chunk ids, so a
    /// topic can never point at content that does not exist.
    /// </summary>
    private static void SaveTopics(SqliteConnection conn, int courseId, List<LlmTopic> llmTopics, List<SectionRow> sections)
    {
        using var tx = conn.BeginTransaction();
        int sequence = 1;

        foreach (var t in llmTopics)
        {
            var chunkIds = new List<int>();
            foreach (var section in sections.Where(s => t.SectionIds.Contains(s.Id)))
            {
                foreach (var chunkIndex in section.ChunkIndexes)
                {
                    var chunkId = Scalar(conn,
                        "SELECT id FROM chunks WHERE document_id = $d AND chunk_index = $i",
                        ("$d", section.DocumentId), ("$i", chunkIndex));
                    if (chunkId is not null) chunkIds.Add(Convert.ToInt32(chunkId));
                }
            }

            // Skip topics with no title or no real content behind them.
            if (chunkIds.Count == 0 || string.IsNullOrWhiteSpace(t.Title)) continue;

            Exec(conn, """
                INSERT INTO topics (course_id, title, description, sequence_number, chunk_ids)
                VALUES ($c, $title, $summary, $seq, $chunks)
                """,
                ("$c", courseId), ("$title", t.Title.Trim()), ("$summary", t.Summary.Trim()),
                ("$seq", sequence++), ("$chunks", JsonSerializer.Serialize(chunkIds.Distinct())));
        }

        tx.Commit();
    }

    // ══ Lesson loop ══════════════════════════════════════════════════════════
    //
    // A session walks through the topic's lesson steps. Its position is stored
    // in the sessions row (phase, step_index, attempt), so a page refresh or a
    // server restart resumes exactly where the learner was.
    //
    //   phase 'lesson' → learner reads the lesson            → Continue()
    //   phase 'quiz'   → learner answers the 3 questions     → SubmitAnswers()
    //        pass (0 or 1 mistakes) → next step, phase 'lesson'
    //        fail (2+ mistakes)     → same step, attempt + 1, new simpler lesson and new questions
    //   phase 'done'   → all steps finished

    /// <summary>A topic is taught in at most this many lesson steps.</summary>
    private const int MaxSteps = 6;

    /// <summary>Returns what the learner should see now, generating the lesson if needed.</summary>
    public async Task<StepResponse?> GetStepAsync(int courseId, int sessionId, CancellationToken ct)
    {
        await LlmLock.WaitAsync(ct);
        try
        {
            using var conn = db.CreateConnection();
            var session = LoadSession(conn, courseId, sessionId);
            if (session is null) return null;
            var topic = LoadTopic(conn, session.TopicId);
            int totalSteps = TotalSteps(topic);

            var step = new StepResponse
            {
                Phase = session.Phase,
                MockMode = MockMode,
                TopicTitle = topic.Title,
                Level = topic.DifficultyLevel,
                StyleLabel = "Standard",
                StepNumber = Math.Min(session.StepIndex + 1, totalSteps),
                TotalSteps = totalSteps,
                Reason = session.Reason,
            };

            if (session.Phase == "done")
            {
                step.Type = "done";
                step.Message = $"You have finished all {totalSteps} lessons of \"{topic.Title}\".";
                return step;
            }

            // Reuse the stored lesson for this step and attempt, or create it now.
            var lesson = FindLesson(conn, session.Id, "lesson", session.StepIndex, session.Attempt)
                ?? await CreateLessonAsync(conn, session, topic, ct);

            if (session.Phase == "lesson")
            {
                step.Type = "lesson";
                step.Lesson = new LessonDto { Id = lesson.Id, Text = lesson.Text, Example = lesson.Example };
            }
            else
            {
                step.Type = "quiz";
                step.Quiz = LoadQuiz(conn, lesson.QuizId);
            }
            return step;
        }
        finally
        {
            LlmLock.Release();
        }
    }

    /// <summary>Learner finished reading: move from the lesson to its quiz.</summary>
    public void Continue(int courseId, int sessionId)
    {
        using var conn = db.CreateConnection();
        var session = LoadSession(conn, courseId, sessionId) ?? throw new LearningException("Session not found.");
        if (session.Phase == "lesson") session.Phase = "quiz";
        SaveSession(conn, session);
    }

    /// <summary>"Explain differently": a new explanation of the same step. Not a failure.</summary>
    public void ExplainDifferently(int courseId, int sessionId)
    {
        using var conn = db.CreateConnection();
        var session = LoadSession(conn, courseId, sessionId) ?? throw new LearningException("Session not found.");
        if (session.Phase != "lesson") throw new LearningException("There is no lesson to re-explain right now.");

        session.Attempt++;
        session.Reason = "Explained in a different way because you asked.";
        SaveSession(conn, session);
    }

    /// <summary>"Skip / I know this": move on without taking the quiz.</summary>
    public void Skip(int courseId, int sessionId)
    {
        using var conn = db.CreateConnection();
        var session = LoadSession(conn, courseId, sessionId) ?? throw new LearningException("Session not found.");
        if (session.Phase is not ("lesson" or "quiz")) throw new LearningException("Nothing to skip right now.");

        AdvanceStep(session, LoadTopic(conn, session.TopicId));
        session.Reason = "You skipped the previous lesson.";
        SaveSession(conn, session);
    }

    /// <summary>
    /// Scores a submitted quiz and decides what happens next.
    /// Scoring and the decision are plain C#; no LLM is involved.
    /// </summary>
    public AnswerResult SubmitAnswers(int courseId, int sessionId, int quizId, SubmitAnswersRequest request)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        var session = LoadSession(conn, courseId, sessionId) ?? throw new LearningException("Session not found.");
        var topic = LoadTopic(conn, session.TopicId);

        var kind = Scalar(conn,
            "SELECT kind FROM learn_quizzes WHERE id = $q AND session_id = $s AND completed_at IS NULL",
            ("$q", quizId), ("$s", sessionId)) as string
            ?? throw new LearningException("This quiz was already submitted or does not exist.");

        var questions = LoadQuestionRows(conn, quizId);

        // Demo mode: pick a wrong option for every question (clearly flagged as simulated).
        var answers = request.SimulateWrong
            ? questions.Select(q => (q.CorrectIndex + 1) % q.OptionCount).ToList()
            : request.Answers;
        if (answers.Count != questions.Count)
            throw new LearningException("Please answer every question.");

        // ── Score ──
        var result = new AnswerResult
        {
            Total = questions.Count,
            Simulated = request.SimulateWrong,
            OldLevel = topic.DifficultyLevel,
            NewLevel = topic.DifficultyLevel,
        };
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            bool isCorrect = answers[i] == q.CorrectIndex;
            if (isCorrect) result.Correct++;

            Exec(conn, "UPDATE learn_questions SET user_answer = $a, is_correct = $c WHERE id = $id",
                ("$a", answers[i]), ("$c", isCorrect ? 1 : 0), ("$id", q.Id));

            result.Questions.Add(new QuestionFeedback
            {
                QuestionId = q.Id,
                UserAnswer = answers[i],
                CorrectIndex = q.CorrectIndex,
                IsCorrect = isCorrect,
                Explanation = q.Explanation,
            });
        }

        // ── Decide ──
        DecideAfterLessonQuiz(conn, session, topic, quizId, result);

        Exec(conn, """
            UPDATE learn_quizzes
            SET correct = $c, passed = $p, simulated = $sim, completed_at = CURRENT_TIMESTAMP
            WHERE id = $id
            """,
            ("$c", result.Correct), ("$p", result.Passed ? 1 : 0),
            ("$sim", request.SimulateWrong ? 1 : 0), ("$id", quizId));
        SaveSession(conn, session);
        tx.Commit();
        return result;
    }

    /// <summary>
    /// The adaptive branch after a lesson quiz:
    ///   at most 1 mistake → praise + one insight, go to the next step;
    ///   otherwise         → teach the same step again, more simply, with new questions.
    /// </summary>
    private void DecideAfterLessonQuiz(
        SqliteConnection conn, LearnSession session, Topic topic, int quizId, AnswerResult result)
    {
        int mistakes = result.Total - result.Correct;
        result.Passed = mistakes <= 1;

        if (result.Passed)
        {
            result.Message = mistakes == 0
                ? "Excellent, every answer was correct!"
                : "Good work, you passed with just one slip.";
            result.Insight = Scalar(conn, """
                SELECT l.insight FROM learn_lessons l
                JOIN learn_quizzes q ON q.lesson_id = l.id WHERE q.id = $q
                """, ("$q", quizId)) as string;

            AdvanceStep(session, topic);
            session.Reason = null;
            result.Decision = session.Phase == "done" ? "done" : "advance";
        }
        else
        {
            session.Attempt++;
            session.Phase = "lesson";
            session.Reason = $"Re-explained in a simpler way because you missed {mistakes} of {result.Total} questions.";
            result.Decision = "reteach";
            result.Message = "Not quite there yet. Let's go through this part again in a simpler way, then try new questions.";
        }
    }

    /// <summary>Moves the session to the next lesson step, or finishes the topic.</summary>
    private static void AdvanceStep(LearnSession session, Topic topic)
    {
        session.StepIndex++;
        session.Attempt = 1;
        session.Phase = "lesson";

        if (session.StepIndex >= TotalSteps(topic))
        {
            session.Phase = "done";
            session.Status = "completed";
        }
    }

    // ── Lesson generation ─────────────────────────────────────────────────────

    private sealed record LessonRow(int Id, int QuizId, string Text, string? Example);

    private static LessonRow? FindLesson(SqliteConnection conn, int sessionId, string kind, int stepIndex, int attempt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.id, q.id, l.text, l.example
            FROM learn_lessons l JOIN learn_quizzes q ON q.lesson_id = l.id
            WHERE l.session_id = $s AND l.kind = $k AND l.step_index = $i AND l.attempt = $a
            ORDER BY l.id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", stepIndex);
        cmd.Parameters.AddWithValue("$a", attempt);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new LessonRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))
            : null;
    }

    /// <summary>Asks the LLM for a lesson and its quiz, then stores both.</summary>
    private async Task<LessonRow> CreateLessonAsync(
        SqliteConnection conn, LearnSession session, Topic topic, CancellationToken ct)
    {
        var source = StepSource(conn, topic, session.StepIndex);

        // On a re-teach, tell the LLM what was already tried and what was missed.
        string? previousText = null;
        List<string> missed = [];
        if (session.Attempt > 1)
        {
            var previous = FindLesson(conn, session.Id, "lesson", session.StepIndex, session.Attempt - 1);
            previousText = previous?.Text;
            if (previous is not null) missed = LoadMissedQuestions(conn, previous.QuizId);
        }

        var style = session.Attempt == 1 ? Prompts.StandardStyle : Prompts.SimplerStyle;
        var reply = await AskAsync<LlmLesson>(
            Prompts.Lesson(topic.Title, source, style, previousText, missed), ct);

        var questions = ValidQuestions(reply.Questions).Take(Prompts.QuizSize).ToList();
        if (string.IsNullOrWhiteSpace(reply.LessonText) || questions.Count < 2)
            throw new LlmException("The AI returned an incomplete lesson. Please retry.");

        using var tx = conn.BeginTransaction();
        int lessonId = Convert.ToInt32(Scalar(conn, """
            INSERT INTO learn_lessons (session_id, topic_id, kind, step_index, attempt, level, text, example, insight, reason)
            VALUES ($s, $t, 'lesson', $i, $a, $lvl, $text, $ex, $ins, $reason) RETURNING id
            """,
            ("$s", session.Id), ("$t", topic.Id), ("$i", session.StepIndex), ("$a", session.Attempt),
            ("$lvl", topic.DifficultyLevel), ("$text", reply.LessonText.Trim()),
            ("$ex", reply.Example), ("$ins", reply.Insight), ("$reason", session.Reason)));
        int quizId = InsertQuiz(conn, session.Id, topic.Id, lessonId, "lesson", questions);
        tx.Commit();

        return new LessonRow(lessonId, quizId, reply.LessonText.Trim(), reply.Example);
    }

    /// <summary>Drops malformed questions (the LLM's JSON is checked, not trusted).</summary>
    private static IEnumerable<LlmQuestion> ValidQuestions(IEnumerable<LlmQuestion> questions) =>
        questions.Where(q =>
            !string.IsNullOrWhiteSpace(q.Text)
            && q.Options.Count >= 2
            && q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count);

    private static int InsertQuiz(
        SqliteConnection conn, int sessionId, int topicId, int? lessonId, string kind, List<LlmQuestion> questions)
    {
        int quizId = Convert.ToInt32(Scalar(conn, """
            INSERT INTO learn_quizzes (session_id, topic_id, lesson_id, kind, total)
            VALUES ($s, $t, $l, $k, $n) RETURNING id
            """,
            ("$s", sessionId), ("$t", topicId), ("$l", lessonId), ("$k", kind), ("$n", questions.Count)));

        foreach (var q in questions)
            Exec(conn, """
                INSERT INTO learn_questions (quiz_id, topic_id, text, options, correct_index, explanation)
                VALUES ($q, $t, $text, $opts, $ci, $ex)
                """,
                ("$q", quizId), ("$t", topicId), ("$text", q.Text.Trim()),
                ("$opts", JsonSerializer.Serialize(q.Options)), ("$ci", q.CorrectIndex), ("$ex", q.Explanation));

        return quizId;
    }

    // ── Source text for a lesson step ─────────────────────────────────────────

    private static int TotalSteps(Topic topic) => Math.Clamp(topic.ChunkIds.Count, 1, MaxSteps);

    /// <summary>
    /// The uploaded text a lesson step is grounded in. The topic's chunks are
    /// spread evenly over its steps (chunk i belongs to step i * steps / count).
    /// </summary>
    private static string StepSource(SqliteConnection conn, Topic topic, int stepIndex)
    {
        int steps = TotalSteps(topic);
        var texts = topic.ChunkIds
            .Where((_, i) => i * steps / topic.ChunkIds.Count == stepIndex)
            .Select(id => Scalar(conn, "SELECT text FROM chunks WHERE id = $id", ("$id", id)) as string)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var source = string.Join("\n\n", texts);
        if (source.Length == 0)
            throw new LearningException("The source text for this lesson is missing. Was the document removed?");

        // Keep the prompt a bounded size.
        return source.Length > 6000 ? source[..6000] : source;
    }

    // ── Session / topic / quiz rows ───────────────────────────────────────────

    private static LearnSession? LoadSession(SqliteConnection conn, int courseId, int sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, course_id, current_topic_id, status, phase, step_index, attempt,
                   since_checkpoint, checkpoint_every, checkpoint_from, review_round, reason
            FROM sessions WHERE id = $id AND course_id = $c AND current_topic_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$c", courseId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new LearnSession
        {
            Id = r.GetInt32(0),
            CourseId = r.GetInt32(1),
            TopicId = r.GetInt32(2),
            Status = r.GetString(3),
            Phase = r.GetString(4),
            StepIndex = r.GetInt32(5),
            Attempt = r.GetInt32(6),
            SinceCheckpoint = r.GetInt32(7),
            CheckpointEvery = r.GetInt32(8),
            CheckpointFrom = r.GetInt32(9),
            ReviewRound = r.GetInt32(10),
            Reason = r.IsDBNull(11) ? null : r.GetString(11),
        };
    }

    private static void SaveSession(SqliteConnection conn, LearnSession s)
    {
        Exec(conn, """
            UPDATE sessions
            SET status = $status, phase = $phase, step_index = $step, attempt = $attempt,
                since_checkpoint = $since, checkpoint_every = $every, checkpoint_from = $from,
                review_round = $round, reason = $reason,
                ended_at = CASE WHEN $status = 'completed' THEN COALESCE(ended_at, CURRENT_TIMESTAMP) ELSE NULL END
            WHERE id = $id
            """,
            ("$status", s.Status), ("$phase", s.Phase), ("$step", s.StepIndex), ("$attempt", s.Attempt),
            ("$since", s.SinceCheckpoint), ("$every", s.CheckpointEvery), ("$from", s.CheckpointFrom),
            ("$round", s.ReviewRound), ("$reason", s.Reason), ("$id", s.Id));

        // Keep the topic's status in step with the session (a weak topic stays weak).
        if (s.Status == "completed")
            Exec(conn, "UPDATE topics SET status = 'completed' WHERE id = $t AND status <> 'weak'", ("$t", s.TopicId));
    }

    private static Topic LoadTopic(SqliteConnection conn, int topicId)
    {
        var courseId = Convert.ToInt32(Scalar(conn, "SELECT course_id FROM topics WHERE id = $t", ("$t", topicId)));
        return LoadTopics(conn, courseId).First(t => t.Id == topicId);
    }

    private sealed record QuestionRow(int Id, int TopicId, int CorrectIndex, int OptionCount, string? Explanation);

    private static List<QuestionRow> LoadQuestionRows(SqliteConnection conn, int quizId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, topic_id, correct_index, options, explanation FROM learn_questions WHERE quiz_id = $q ORDER BY id";
        cmd.Parameters.AddWithValue("$q", quizId);
        var rows = new List<QuestionRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new QuestionRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                JsonSerializer.Deserialize<List<string>>(r.GetString(3))!.Count,
                r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    /// <summary>A quiz as sent to the browser: the correct answers are NOT included.</summary>
    private static QuizDto LoadQuiz(SqliteConnection conn, int quizId)
    {
        var quiz = new QuizDto
        {
            Id = quizId,
            Kind = (string)Scalar(conn, "SELECT kind FROM learn_quizzes WHERE id = $q", ("$q", quizId))!,
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text, options, label FROM learn_questions WHERE quiz_id = $q ORDER BY id";
        cmd.Parameters.AddWithValue("$q", quizId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            quiz.Questions.Add(new QuestionDto
            {
                Id = r.GetInt32(0),
                Text = r.GetString(1),
                Options = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? [],
                Label = r.IsDBNull(3) ? null : r.GetString(3),
            });
        return quiz;
    }

    /// <summary>Texts of the questions answered wrongly in a quiz.</summary>
    private static List<string> LoadMissedQuestions(SqliteConnection conn, int quizId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text FROM learn_questions WHERE quiz_id = $q AND is_correct = 0";
        cmd.Parameters.AddWithValue("$q", quizId);
        var texts = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) texts.Add(r.GetString(0));
        return texts;
    }

    // ══ Helpers ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a prompt and deserializes the JSON reply into T.
    /// If the reply is not valid JSON of that shape, an LlmException with a clear
    /// message is thrown; the controller turns it into an error the UI can retry.
    /// </summary>
    private async Task<T> AskAsync<T>(string prompt, CancellationToken ct) where T : class
    {
        var raw = await llm.GenerateAsync(prompt, ct);
        try
        {
            // Some models wrap JSON in ```json fences even when asked not to.
            var json = raw.Trim();
            if (json.StartsWith("```"))
                json = json[(json.IndexOf('\n') + 1)..json.LastIndexOf("```")];

            return JsonSerializer.Deserialize<T>(json)
                ?? throw new JsonException("Reply was null.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            log.LogWarning("Could not parse LLM reply as {Type}: {Msg}", typeof(T).Name, ex.Message);
            throw new LlmException("The AI reply was not in the expected format. Please retry.", ex);
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>A request that cannot be carried out (bad state or input); becomes HTTP 400.</summary>
public sealed class LearningException(string message) : Exception(message);
