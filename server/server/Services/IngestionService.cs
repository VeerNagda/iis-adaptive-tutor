using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using server.Data;
using server.LLM;
using server.Models;

namespace server.Services;

/// <summary>
/// Runs the full ingestion pipeline for one uploaded document and provides chunk search.
///
/// Pipeline steps:
///   1. Extract text and embedded images per page (TextExtractor — PdfPig / OpenXml).
///   1b. Ask the LLM to describe the images; add the descriptions to the page text.
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

    // ── Scope check: is this the kind of file the tutor handles well? ─────────
    //
    // Run before ingestion, with no LLM call. The tutor is built for lecture
    // notes and slides. A whole textbook, or a scan with no real text, would be
    // accepted silently and then taught badly, so the learner is told first and
    // chooses what to do. The limits are the plain constants below.

    /// <summary>More pages (or slides) than this looks like a textbook, not a lecture.</summary>
    public const int WarnAbovePages = 50;
    /// <summary>More words than this looks like a textbook, not a lecture.</summary>
    public const int WarnAboveWords = 25_000;
    /// <summary>Warn when more than this share of the pages has no readable text (a scan).</summary>
    public const double WarnNoTextShare = 0.30;
    /// <summary>A page with fewer words than this counts as having no text.</summary>
    public const int NoTextBelowWords = 5;
    /// <summary>Default for "Ingest first N pages": roughly one lecture.</summary>
    public const int SuggestedFirstPages = 30;
    /// <summary>Words per chunk (also used to estimate the number of chunks).</summary>
    public const int ChunkTargetWords = 400;

    /// <summary>Counts pages and words of an uploaded file and lists any warnings.</summary>
    public ScopeReport CheckScope(int documentId)
    {
        var (filePath, fileType, _) = GetDocument(documentId);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not on disk: {filePath}");

        var pages = TextExtractor.Extract(filePath, fileType, includeImages: false);
        var wordsPerPage = pages.Select(p => WordCount(p.Text)).ToList();

        var scope = new ScopeReport
        {
            Unit = fileType == "pptx" ? "slides" : "pages",
            Pages = pages.Count,
            PagesWithoutText = wordsPerPage.Count(w => w < NoTextBelowWords),
            Words = wordsPerPage.Sum(),
            SuggestedFirstPages = Math.Min(SuggestedFirstPages, pages.Count),
        };
        scope.EstimatedChunks = (int)Math.Ceiling(scope.Words / (double)ChunkTargetWords);

        if (scope.Pages > WarnAbovePages || scope.Words > WarnAboveWords)
            scope.Warnings.Add(
                $"This looks like a textbook ({scope.Pages} {scope.Unit}, about {scope.Words:N0} words). "
                + "The tutor works best with lecture notes or slides. "
                + "Pick a chapter range, or upload your lecture material instead.");

        if (scope.Pages > 0 && scope.PagesWithoutText > scope.Pages * WarnNoTextShare)
            scope.Warnings.Add(
                $"{scope.PagesWithoutText} of {scope.Pages} {scope.Unit} have no text the tutor can read, "
                + "so this may be a scan or mostly pictures. "
                + $"The tutor can describe at most {MaxImagesPerDocument} pictures per file in words, so a lot may be missed. "
                + "A version with real text will work much better.");

        return scope;
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Ingests a document. fromPage and toPage (1-based, inclusive) limit it to a
    /// page range chosen by the learner; null means the whole file.
    /// </summary>
    public async Task IngestAsync(int documentId, int? fromPage = null, int? toPage = null, CancellationToken ct = default)
    {
        log.LogInformation("Ingesting document {Id}", documentId);
        SetStatus(documentId, "processing");

        // The report is filled in as each step returns, from that step's actual
        // result, and is saved whether ingestion finishes or fails. "step" names
        // the step in progress, so a failure can say exactly where it stopped.
        var report = new IngestReport { FromPage = fromPage, ToPage = toPage };
        string step = "reading the file";

        try
        {
            var (filePath, fileType, courseId) = GetDocument(documentId);
            report.Unit = fileType == "pptx" ? "slides" : "pages";

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not on disk: {filePath}");

            // Step 1 – extract text and images, keeping only the chosen page range
            var all = TextExtractor.Extract(filePath, fileType);
            var extracted = all
                .Where(p => p.PageNumber >= (fromPage ?? 1) && p.PageNumber <= (toPage ?? int.MaxValue))
                .ToList();
            report.PagesTotal = all.Count;
            report.PagesOutsideRange = all.Count - extracted.Count;
            report.ImagesUnreadable = extracted.Sum(p => p.UnreadableImages);

            // Step 1b – the LLM describes the images; the descriptions join the page text
            step = "describing the pictures";
            var pages = await DescribeImagesAsync(extracted, report, ct);
            report.PagesRead = pages.Count;
            report.PagesNoText = extracted.Count - pages.Count;
            if (pages.Count == 0)
                throw new InvalidOperationException("No text or usable images could be extracted from the file.");

            // Step 2 – chunk
            step = "splitting the text into chunks";
            var chunks = SplitIntoChunks(pages, ChunkTargetWords);
            log.LogInformation("{Count} chunks from {Pages} pages", chunks.Count, pages.Count);

            // Step 3 – generate sections
            step = "grouping the text into sections";
            var sections = await GenerateSectionsAsync(chunks, ct);

            // Step 4 – embed
            step = "indexing the text";
            log.LogInformation("Embedding {Count} chunks…", chunks.Count);
            var embeddings = await embedder.EmbedAsync(
                chunks.Select(c => c.Text).ToList(), "RETRIEVAL_DOCUMENT", ct);

            // Step 5 – store
            step = "saving";
            DeleteExisting(documentId);
            StoreChunks(documentId, chunks, embeddings);
            StoreSections(documentId, sections);

            SaveReport(documentId, report);
            SetStatus(documentId, "completed");
            log.LogInformation("✓ Document {Id} done: {C} chunks, {S} sections",
                documentId, chunks.Count, sections.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ingestion failed for document {Id}", documentId);
            report.Failure = $"It stopped while {step}: {ex.Message}";
            SaveReport(documentId, report);
            SetStatus(documentId, "failed", ex.Message);
            throw;
        }
    }

    // ── Ingestion report ───────────────────────────────────────────────────────
    //
    // "Done" is only a claim until the numbers behind it are shown. The file card
    // therefore states what was read and, separately, what was NOT: pages left
    // out, pictures skipped and why, and the step where a failure happened. The
    // sentences are built here from the counts; the LLM writes none of them.

    private void SaveReport(int documentId, IngestReport report)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE documents SET ingest_report = @r WHERE id = @id";
        cmd.Parameters.AddWithValue("@r", JsonSerializer.Serialize(report));
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Turns the stored counts into the sentences shown on the file card.
    /// chunks, sections and topics are counted from the database at the time of
    /// asking, so they are what is really stored, not what a step said it would store.
    /// </summary>
    public static DocumentReport Describe(
        IngestReport r, string status, int chunks, int sections, int topics, bool topicsExtracted)
    {
        var text = new DocumentReport();
        string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        string unitOne = r.Unit == "slides" ? "slide" : "page";
        int inRange = r.PagesTotal - r.PagesOutsideRange;

        if (r.Failure is not null)
        {
            text.NotDone.Add($"Ingestion did not finish. {r.Failure}");
            text.NotDone.Add("Nothing from this file is being taught. Press Retry to run it again.");
            if (r.PagesRead > 0) text.Done.Add($"Before it stopped: read {r.PagesRead} of {inRange} {r.Unit}.");
            if (r.ImagesDescribed > 0) text.Done.Add($"Before it stopped: {Plural(r.ImagesDescribed, "picture", "pictures")} described.");
            return text;
        }
        if (status != "completed") return text;   // not ingested yet: nothing to report

        // Pages
        text.Done.Add($"Read {r.PagesRead} of {r.PagesTotal} {r.Unit}.");
        if (r.PagesOutsideRange > 0)
            text.NotDone.Add($"{Plural(r.PagesOutsideRange, unitOne, r.Unit)} outside your chosen range "
                + $"({r.FromPage ?? 1}-{r.ToPage ?? r.PagesTotal}) not read. To add them, remove the file and ingest it again with a wider range.");
        if (r.PagesNoText > 0)
            text.NotDone.Add($"{Plural(r.PagesNoText, unitOne, r.Unit)} had no text or usable picture and "
                + (r.PagesNoText == 1 ? "was" : "were") + " skipped.");

        // Pictures: described, and each reason for a skip with its own count
        if (r.ImagesFound + r.ImagesUnreadable > 0)
        {
            var skipped = new List<string>();
            if (r.ImagesTooSmall > 0) skipped.Add($"{r.ImagesTooSmall} under {MinImageBytes / 1000} KB");
            if (r.ImagesDuplicate > 0) skipped.Add($"{Plural(r.ImagesDuplicate, "repeat", "repeats")}");
            if (r.ImagesDecoration > 0) skipped.Add($"{r.ImagesDecoration} judged to be decoration by the AI");
            int skippedCount = r.ImagesTooSmall + r.ImagesDuplicate + r.ImagesDecoration;
            text.Done.Add($"{Plural(r.ImagesDescribed, "picture", "pictures")} described in words"
                + (skippedCount > 0 ? $", {skippedCount} skipped ({string.Join(", ", skipped)})." : "."));

            if (r.ImagesOverLimit > 0)
                text.NotDone.Add($"{Plural(r.ImagesOverLimit, "picture was", "pictures were")} not described because of the limit of {MaxImagesPerDocument} per file.");
            if (r.ImagesTooLarge > 0)
                text.NotDone.Add($"{Plural(r.ImagesTooLarge, "picture", "pictures")} over {MaxImageBytes / 1_000_000} MB not described.");
            if (r.ImagesUnreadable > 0)
                text.NotDone.Add($"{Plural(r.ImagesUnreadable, "picture is", "pictures are")} in a format the tutor can't read.");
            if (r.ImagesNoReply > 0)
                text.NotDone.Add($"The AI gave no description for {Plural(r.ImagesNoReply, "picture", "pictures")} it was sent.");
        }

        // What is stored now
        text.Done.Add($"Made {Plural(chunks, "chunk", "chunks")} and {Plural(sections, "section", "sections")}.");
        if (topicsExtracted)
            text.Done.Add($"{Plural(topics, "topic", "topics")} use this file.");
        else
            text.NotDone.Add("Topics have not been made from this file yet. They are added when the topic list loads.");

        return text;
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

    // ── Step 1b: Image descriptions ────────────────────────────────────────────
    //
    // The rest of the system works on text only, so each picture is turned into
    // text once, here: the LLM looks at the image and writes a description, which
    // is added to the text of the page it came from as
    //     [Image on this page: ...]
    // From then on it is chunked, grouped into topics and taught like any other
    // sentence of the document. The image itself is not stored or shown.
    //
    // Which images are sent is decided by the plain rules below (not by the LLM),
    // to keep the number of LLM calls small and predictable.

    /// <summary>Smaller files are almost always icons, bullets or lines.</summary>
    public const int MinImageBytes = 5_000;
    /// <summary>Larger files are skipped to keep the request within the API's size limit.</summary>
    public const int MaxImageBytes = 2_000_000;
    /// <summary>At most this many images are described per document.</summary>
    public const int MaxImagesPerDocument = 20;
    /// <summary>Images sent in one LLM call (so 20 images cost 4 calls, not 20).</summary>
    private const int ImagesPerCall = 5;

    private async Task<List<TextExtractor.PageText>> DescribeImagesAsync(
        IReadOnlyList<TextExtractor.PageText> pages, IngestReport report, CancellationToken ct)
    {
        // Pick the images worth describing, remembering which page each is on.
        // Every image that is not picked is counted under the rule that left it out.
        var picked = new List<(int PageIndex, LlmImage Image)>();
        var seen = new HashSet<string>();   // an image repeated on every slide (a logo) is sent once
        for (int p = 0; p < pages.Count; p++)
        {
            foreach (var image in pages[p].Images)
            {
                report.ImagesFound++;
                if (image.Data.Length < MinImageBytes) report.ImagesTooSmall++;
                else if (image.Data.Length > MaxImageBytes) report.ImagesTooLarge++;
                else if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Data)))) report.ImagesDuplicate++;
                else if (picked.Count >= MaxImagesPerDocument) report.ImagesOverLimit++;
                else picked.Add((p, image));
            }
        }
        log.LogInformation("{Found} images found, {Picked} sent for description", report.ImagesFound, picked.Count);

        // One LLM call per batch; each description goes back to its page.
        var described = pages.Select(_ => new StringBuilder()).ToList();
        foreach (var batch in picked.Chunk(ImagesPerCall))
        {
            var json = await llm.GenerateAsync(
                Prompts.Images(batch.Length), batch.Select(b => b.Image).ToList(), ct);

            List<string> descriptions;
            try
            {
                descriptions = JsonSerializer.Deserialize<LlmImageDescriptions>(LlmJson.Extract(json))?.Descriptions ?? [];
            }
            catch (JsonException ex)
            {
                throw new LlmException("The AI reply for the images was not in the expected format. Please retry.", ex);
            }

            for (int i = 0; i < batch.Length; i++)
            {
                // Counted from what actually came back, not from what was asked for.
                if (i >= descriptions.Count) { report.ImagesNoReply++; continue; }
                // An empty description means the LLM judged the image to be decoration.
                if (string.IsNullOrWhiteSpace(descriptions[i])) { report.ImagesDecoration++; continue; }
                // Kept on one line so the chunker treats it as one paragraph.
                var oneLine = descriptions[i].ReplaceLineEndings(" ").Trim();
                described[batch[i].PageIndex].AppendLine($"[Image on this page: {oneLine}]");
                report.ImagesDescribed++;
            }
        }

        // Text first, then the image descriptions. The image bytes are no longer
        // needed; pages that still have nothing to teach are dropped.
        return pages
            .Select((page, i) => page with { Text = $"{page.Text}\n{described[i]}".Trim(), Images = [] })
            .Where(page => page.Text.Length > 0)
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

        // All prompts live in LLM/Prompts.cs so they can be inspected in one place.
        var prompt = Prompts.Sections(chunkSummary);

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
        using var doc = JsonDocument.Parse(LlmJson.Extract(json));
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
