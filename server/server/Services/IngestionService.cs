using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using server.Data;
using server.LLM;

namespace server.Services;

/// <summary>
/// Runs the full ingestion pipeline for one uploaded document and provides chunk search.
///
/// Pipeline steps:
///   1. Extract text per page (TextExtractor — PdfPig / OpenXml).
///   2. Split pages into ~400-word chunks in C# (paragraph-aware).
///   3. Call ILlmProvider to generate 4–8 learning sections from the chunk list.
///   4. Embed all chunk texts via IGeminiEmbedder (768-dim, RETRIEVAL_DOCUMENT).
///   5. Write chunks (with embeddings as JSON) and sections to SQLite.
///
/// Search: embed the query (RETRIEVAL_QUERY), load stored embeddings, rank by cosine
/// similarity in-memory. For the document sizes in this prototype this is fast enough.
/// </summary>
public sealed class IngestionService(
    IDbConnectionFactory db,
    ILlmProvider llm,
    IGeminiEmbedder embedder,
    ILogger<IngestionService> log)
{
    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task IngestAsync(int documentId, CancellationToken ct = default)
    {
        log.LogInformation("Ingesting document {Id}", documentId);
        SetStatus(documentId, "processing");

        try
        {
            var (filePath, fileType, courseId) = GetDocument(documentId);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not on disk: {filePath}");

            // Step 1 – extract text
            var pages = TextExtractor.Extract(filePath, fileType);
            if (pages.Count == 0)
                throw new InvalidOperationException("No text could be extracted from the file.");

            // Step 2 – chunk
            var chunks = SplitIntoChunks(pages, targetWords: 400);
            log.LogInformation("{Count} chunks from {Pages} pages", chunks.Count, pages.Count);

            // Step 3 – generate sections
            var sections = await GenerateSectionsAsync(chunks, ct);

            // Step 4 – embed
            log.LogInformation("Embedding {Count} chunks…", chunks.Count);
            var embeddings = await embedder.EmbedAsync(
                chunks.Select(c => c.Text).ToList(), "RETRIEVAL_DOCUMENT", ct);

            // Step 5 – store
            DeleteExisting(documentId);
            StoreChunks(documentId, chunks, embeddings);
            StoreSections(documentId, sections);

            SetStatus(documentId, "completed");
            log.LogInformation("✓ Document {Id} done: {C} chunks, {S} sections",
                documentId, chunks.Count, sections.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ingestion failed for document {Id}", documentId);
            SetStatus(documentId, "failed", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Returns the top-k chunks most relevant to <paramref name="query"/>,
    /// optionally restricted to a single document.
    /// Embeds the query with RETRIEVAL_QUERY task type, then ranks all stored
    /// chunk embeddings by cosine similarity in memory.
    /// </summary>
    public async Task<IReadOnlyList<ChunkResult>> SearchAsync(
        string query,
        int? documentId = null,
        int k = 5,
        CancellationToken ct = default)
    {
        var vecs = await embedder.EmbedAsync([query], "RETRIEVAL_QUERY", ct);
        var q = vecs[0];

        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();

        if (documentId.HasValue)
        {
            cmd.CommandText = "SELECT id, document_id, text, heading, page_start, page_end, embedding FROM chunks WHERE document_id = @did";
            cmd.Parameters.AddWithValue("@did", documentId.Value);
        }
        else
        {
            cmd.CommandText = "SELECT id, document_id, text, heading, page_start, page_end, embedding FROM chunks";
        }

        var results = new List<(ChunkResult chunk, float score)>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var embJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var vec = embJson is null ? null : JsonSerializer.Deserialize<float[]>(embJson);
            var score = vec is null ? 0f : CosineSimilarity(q, vec);

            results.Add((new ChunkResult(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                score), score));
        }

        return results
            .OrderByDescending(r => r.score)
            .Take(k)
            .Select(r => r.chunk)
            .ToList();
    }

    // ── Step 2: Chunking ───────────────────────────────────────────────────────

    private static List<Chunk> SplitIntoChunks(IReadOnlyList<TextExtractor.PageText> pages, int targetWords)
    {
        var chunks = new List<Chunk>();
        var buf = new StringBuilder();
        int wordCount = 0;
        int chunkStart = pages[0].PageNumber;
        int chunkEnd = pages[0].PageNumber;
        string? heading = null;

        void Flush()
        {
            var text = buf.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(new Chunk(text, chunkStart, chunkEnd, heading));
            buf.Clear();
            wordCount = 0;
            heading = null;
        }

        foreach (var page in pages)
        {
            // Split page text into paragraphs (double-newline or single newline)
            var paragraphs = page.Text
                .Split(["\r\n\r\n", "\n\n", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p));

            foreach (var para in paragraphs)
            {
                var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Detect a heading: short line, mostly title-cased or uppercase
                if (words.Length <= 10 && wordCount > 0 &&
                    para.Length < 80 && char.IsUpper(para[0]))
                {
                    Flush();
                    chunkStart = page.PageNumber;
                    heading = para;
                    chunkEnd = page.PageNumber;
                    continue;
                }

                buf.AppendLine(para);
                wordCount += words.Length;
                chunkEnd = page.PageNumber;

                if (wordCount >= targetWords)
                {
                    Flush();
                    chunkStart = page.PageNumber;
                    chunkEnd = page.PageNumber;
                }
            }
        }

        Flush();
        return chunks;
    }

    // ── Step 3: Section generation ─────────────────────────────────────────────

    private async Task<List<Section>> GenerateSectionsAsync(List<Chunk> chunks, CancellationToken ct)
    {
        // Build a compact chunk list to stay within prompt token limits.
        // Truncate each chunk to 400 chars; full text is stored in the DB.
        var chunkSummary = string.Join("\n\n",
            chunks.Select((c, i) =>
                $"[Chunk {i}] p.{c.PageStart}  {c.Text[..Math.Min(c.Text.Length, 400)]}"));

        // $$""" lets us use {{ }} as literal braces in the JSON example while still
        // interpolating {{chunkSummary}} with double-braces for the variable.
        var prompt = $$"""
            SECTION_SPLIT
            You are an educational content designer.
            Given these numbered text chunks from a study document, group them into 4–8 learning
            sections ordered from foundational to advanced.
            For each section provide:
              title (clear heading), summary (2 sentences), key_concepts (3-6 concept names),
              difficulty (1=introductory, 2=intermediate, 3=advanced),
              chunk_indexes (0-based list covering every chunk — no chunk may be unassigned).
            Return JSON with a "sections" array where each object has the five fields above.

            {{chunkSummary}}
            """;

        // Retry once only on JSON parse failures (bad schema from the model).
        // HTTP errors (429, 503, etc.) are already retried with backoff inside GeminiProvider
        // and should propagate as LlmException so the caller sees the real error.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var json = await llm.GenerateAsync(prompt, ct);
            try
            {
                return ParseSections(json);
            }
            catch (Exception ex) when (ex is not LlmException && attempt == 0)
            {
                log.LogWarning("Section JSON parse failed, retrying once: {Msg}", ex.Message);
            }
        }

        throw new InvalidOperationException("Section generation failed after retry.");
    }

    private static List<Section> ParseSections(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.GetProperty("sections");
        var sections = new List<Section>();

        foreach (var s in root.EnumerateArray())
        {
            sections.Add(new Section(
                s.GetProperty("title").GetString() ?? "Section",
                s.TryGetProperty("summary", out var sum) ? sum.GetString() : null,
                s.TryGetProperty("key_concepts", out var kc)
                    ? kc.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [],
                s.TryGetProperty("difficulty", out var diff) ? diff.GetInt32() : 1,
                s.GetProperty("chunk_indexes").EnumerateArray().Select(x => x.GetInt32()).ToList()
            ));
        }

        if (sections.Count == 0)
            throw new InvalidOperationException("LLM returned an empty sections array.");

        return sections;
    }

    // ── Step 5: Storage ────────────────────────────────────────────────────────

    private void DeleteExisting(int documentId)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using var d1 = conn.CreateCommand();
        d1.Transaction = tx;
        d1.CommandText = "DELETE FROM chunks WHERE document_id = @id";
        d1.Parameters.AddWithValue("@id", documentId);
        d1.ExecuteNonQuery();

        using var d2 = conn.CreateCommand();
        d2.Transaction = tx;
        d2.CommandText = "DELETE FROM document_sections WHERE document_id = @id";
        d2.Parameters.AddWithValue("@id", documentId);
        d2.ExecuteNonQuery();

        tx.Commit();
    }

    private void StoreChunks(int documentId, List<Chunk> chunks, IReadOnlyList<float[]> embeddings)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            // Serialise the embedding as a compact JSON float array.
            var embJson = JsonSerializer.Serialize(embeddings[i]);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO chunks (document_id, chunk_index, text, page_start, page_end, heading, embedding)
                VALUES (@did, @idx, @text, @ps, @pe, @h, @emb)
                """;
            cmd.Parameters.AddWithValue("@did",  documentId);
            cmd.Parameters.AddWithValue("@idx",  i);
            cmd.Parameters.AddWithValue("@text", c.Text);
            cmd.Parameters.AddWithValue("@ps",   (object?)c.PageStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pe",   (object?)c.PageEnd   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@h",    (object?)c.Heading   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@emb",  embJson);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void StoreSections(int documentId, List<Section> sections)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO document_sections
                    (document_id, section_index, title, summary, key_concepts, difficulty, chunk_indexes)
                VALUES (@did, @idx, @title, @sum, @kc, @diff, @ci)
                """;
            cmd.Parameters.AddWithValue("@did",   documentId);
            cmd.Parameters.AddWithValue("@idx",   i);
            cmd.Parameters.AddWithValue("@title", s.Title);
            cmd.Parameters.AddWithValue("@sum",   (object?)s.Summary      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kc",    JsonSerializer.Serialize(s.KeyConcepts));
            cmd.Parameters.AddWithValue("@diff",  Math.Clamp(s.Difficulty, 1, 3));
            cmd.Parameters.AddWithValue("@ci",    JsonSerializer.Serialize(s.ChunkIndexes));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ── Status helper ──────────────────────────────────────────────────────────

    private void SetStatus(int documentId, string status, string? error = null)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE documents SET processing_status = @s, error_message = @e WHERE id = @id";
        cmd.Parameters.AddWithValue("@s",  status);
        cmd.Parameters.AddWithValue("@e",  (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.ExecuteNonQuery();
    }

    private (string FilePath, string FileType, int CourseId) GetDocument(int documentId)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, file_type, course_id FROM documents WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", documentId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"Document {documentId} not found.");
        return (r.GetString(0), r.GetString(1), r.GetInt32(2));
    }

    // ── Cosine similarity ──────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-8f);
    }

    // ── Internal record types ──────────────────────────────────────────────────

    private sealed record Chunk(string Text, int? PageStart, int? PageEnd, string? Heading);
    private sealed record Section(
        string Title, string? Summary, List<string> KeyConcepts,
        int Difficulty, List<int> ChunkIndexes);
}

/// <summary>Returned by SearchAsync.</summary>
public sealed record ChunkResult(
    int Id, int DocumentId, string Text,
    string? Heading, int? PageStart, int? PageEnd,
    float Score);
