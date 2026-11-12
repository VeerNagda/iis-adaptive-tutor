namespace server.LLM;

/// <summary>
/// Every prompt sent to the LLM lives in this one file so it is easy to inspect.
///
/// Conventions used by all prompts:
///   - The first line is a task marker such as [[TASK:TOPICS]]. The real model
///     ignores it; MockLlmProvider reads it to know which canned reply to return.
///   - Each prompt ends by spelling out the exact JSON shape wanted. Together
///     with Gemini's responseMimeType=application/json (see GeminiProvider) this
///     is the "structured output": the reply is deserialized directly into the
///     Llm* classes in Models/Learning.cs.
///   - $$""" ... """ is a C# raw string: {{name}} inserts a variable, while
///     single { } are literal braces, which keeps the JSON examples readable.
/// </summary>
public static class Prompts
{
    public const string SectionsTask = "[[TASK:SECTIONS]]";
    public const string TopicsTask = "[[TASK:TOPICS]]";

    // ── Ingestion: group a document's chunks into learning sections ───────────
    public static string Sections(string chunkSummary) => $$"""
        {{SectionsTask}}
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

    // ── Start Learning: turn the ingested sections into a list of topics ──────
    // Input is only titles and summaries (short and cheap); the full text is
    // looked up from the database later, when a lesson is generated.
    public static string Topics(string sectionList) => $$"""
        {{TopicsTask}}
        You are organising a learner's uploaded study material into topics for a tutoring app.
        Below are the sections found in the material, one per line, as: [S<id>] title — summary

        Group them into 3 to 8 topics, ordered from foundational to advanced.
        Rules:
          - Use only the sections listed. Do not invent content.
          - Put each section id in at most one topic.
          - Leave out sections that are only references, reading lists or admin notes.
          - title: short and specific. summary: one or two plain sentences on what the learner will learn.

        Return JSON only, in exactly this shape:
        {"topics":[{"title":"...","summary":"...","section_ids":[1,2]}]}

        SECTIONS:
        {{sectionList}}
        """;
}
