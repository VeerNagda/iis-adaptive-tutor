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
    public const string LessonTask = "[[TASK:LESSON]]";

    /// <summary>Questions per lesson quiz. Pass = at most one mistake (see LearningService).</summary>
    public const int QuizSize = 3;

    // ── Teaching styles ───────────────────────────────────────────────────────
    public const string StandardStyle =
        "Standard explanation: clear, complete sentences, define each key term once, about 150 words.";

    public const string SimplerStyle =
        "Simpler explanation: plain everyday language, short sentences, small steps, "
        + "one analogy from daily life and one worked example, about 120 words.";

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

    // ── Lesson + quiz for one lesson step ─────────────────────────────────────
    // One call returns the lesson AND its quiz. Reasons for a single call:
    //   - the questions are written against the exact lesson text the learner
    //     sees, so they are always answerable from it;
    //   - half the API calls (cost, speed, fewer points of failure).
    //
    // source         text of the uploaded material for this step (the only allowed facts)
    // style          teaching-style instructions (see the styles above)
    // previousLesson set on a re-teach: the explanation that did not work
    // missed         set on a re-teach: the questions the learner got wrong
    public static string Lesson(
        string topicTitle, string source, string style,
        string? previousLesson = null, IReadOnlyList<string>? missed = null)
    {
        // Only added when the same step is taught again.
        var missedList = missed is { Count: > 0 } ? string.Join(" | ", missed) : "none recorded";
        var reteach = previousLesson is null ? "" : $"""

            RE-TEACH: the learner has already seen the explanation below and it did not work.
            Explain the same content again in a clearly different way (new wording, new example).
            Questions they got wrong (focus on these ideas): {missedList}
            Write NEW quiz questions; do not repeat or lightly reword the earlier ones.
            PREVIOUS EXPLANATION: {previousLesson}
            """;

        return $$"""
            {{LessonTask}}
            You are a patient tutor. Teach ONE short lesson that is part of the topic "{{topicTitle}}".

            GROUNDING: use only facts found in SOURCE below. Do not add outside knowledge.
            If SOURCE does not cover something, leave it out.

            TEACHING STYLE (follow closely):
            {{style}}
            {{reteach}}

            After the lesson, write {{QuizSize}} multiple-choice questions that can be answered
            from your lesson_text alone. Each question has 4 options with exactly one correct,
            plausible wrong options, and a 1-2 sentence explanation of why the answer is right.

            Return JSON only, in exactly this shape:
            {"lesson_text":"the lesson",
             "example":"one concrete example or analogy",
             "insight":"one sentence: an extra interesting point from SOURCE that goes slightly beyond the lesson",
             "questions":[{"text":"...","options":["...","...","...","..."],"correct_index":0,"explanation":"..."}]}
            correct_index is the 0-based position of the correct option; vary it between questions.

            <<<SOURCE
            {{source}}
            SOURCE>>>
            """;
    }
}
