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
    private static readonly SemaphoreSlim TopicLock = new(1, 1);

    // ══ Topics ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the course's topics. The first call extracts them with the LLM
    /// from the ingested sections and caches them in the topics table; later
    /// calls only read the table.
    /// </summary>
    public async Task<TopicListResponse> GetTopicsAsync(int courseId, CancellationToken ct)
    {
        await TopicLock.WaitAsync(ct);
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
            TopicLock.Release();
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
