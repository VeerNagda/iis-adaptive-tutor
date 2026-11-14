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
    public const string ImagesTask = "[[TASK:IMAGES]]";
    public const string SectionsTask = "[[TASK:SECTIONS]]";
    public const string TopicsTask = "[[TASK:TOPICS]]";
    public const string LessonTask = "[[TASK:LESSON]]";
    public const string CheckpointTask = "[[TASK:CHECKPOINT]]";
    public const string AskTask = "[[TASK:ASK]]";

    /// <summary>Questions per lesson quiz. Pass = at most one mistake (see LearningService).</summary>
    public const int QuizSize = 3;

    // ── Shape of one quiz question (shared by the lesson and checkpoint prompts) ─
    // option_feedback is what lets the app tell a learner WHY their wrong answer
    // was wrong: the reason for every option is written together with the
    // question, stored, and shown later without a second LLM call.
    private const string QuestionShape =
        """{"text":"...","options":["...","...","...","..."],"correct_index":0,"explanation":"...","option_feedback":["...","...","...","..."]}""";

    private const string QuestionRules =
        "Each question has 4 options with exactly one correct and plausible wrong options. "
        + "explanation: 1-2 sentences on why the correct option is right. "
        + "option_feedback: one short sentence per option, in the same order as options. For a wrong "
        + "option, name the misunderstanding behind it and say why it is wrong; for the correct option, say why it is right. "
        + "correct_index is the 0-based position of the correct option; vary it between questions.";

    // ── Adaptive difficulty: level → teaching style ───────────────────────────
    // Each topic has a difficulty level from 1 (easiest) to 5 (hardest). The
    // level itself is changed by plain C# rules in LearningService; here it is
    // only translated into explicit instructions for the LLM, so "level 2"
    // always means the same thing and can be read off this table.
    public static string StyleForLevel(int level) => level switch
    {
        1 => "Very simple. Plain everyday words, no jargon. Very short sentences. Go step by step in "
           + "small numbered steps. Use one analogy from daily life and two tiny examples. About 120 words. "
           + "Quiz questions: direct recall of a single idea, with clearly different options.",
        2 => "Simple. Mostly plain language; introduce at most two technical terms and explain each in "
           + "brackets. Short sentences, small steps, one analogy and one worked example. About 130 words. "
           + "Quiz questions: recall and simple understanding.",
        3 => "Standard explanation. Clear, complete sentences. Define each key term once. One concrete "
           + "example. About 150 words. Quiz questions: understanding, not just recall.",
        4 => "Advanced. Assume the basics are known. Use the correct technical terms, explain why things "
           + "work and how the ideas connect. About 160 words. Quiz questions: apply the idea to a new situation.",
        _ => "Expert. Concise and technical. Focus on precise definitions, trade-offs, limits and edge "
           + "cases; no basic analogies. About 150 words. Quiz questions: edge cases and comparing close "
           + "alternatives, with subtle wrong options.",
    };

    /// <summary>Short name of each level, shown in the UI next to the level number.</summary>
    public static string StyleLabel(int level) => level switch
    {
        1 => "Very simple",
        2 => "Simple",
        3 => "Standard",
        4 => "Advanced",
        _ => "Expert",
    };

    // ── Ingestion: turn the images of a document into text ────────────────────
    // The images themselves are attached to the request (see GeminiProvider);
    // this prompt only says what to do with them. Everything after ingestion
    // (chunks, topics, lessons, quizzes) works on text, so a picture can only
    // be taught if it is first written down. Design choices:
    //   - "describe only what is visible": the description becomes SOURCE for
    //     lessons, so it must not contain invented facts;
    //   - an empty string for decoration (logos, backgrounds), so those do not
    //     end up in the lessons;
    //   - one string per image, in order, so C# can put each description back
    //     on the page the image came from.
    public static string Images(int count) => $$"""
        {{ImagesTask}}
        You are preparing study material for a tutoring app. Attached are {{count}} images taken
        from a learner's uploaded lecture file, in order.

        For each image write a description that a tutor could teach from, in at most 120 words:
          - say what kind of image it is (diagram, chart, table, screenshot, photo, scanned page);
          - say what it shows: its parts, labels and values, and how they relate to each other;
          - copy any important text in the image word for word.
        Describe only what is visible. Do not guess and do not add outside knowledge.
        If an image is only decoration (logo, icon, background, a photo with no teaching content),
        return an empty string for it.

        Return JSON only, in exactly this shape, with exactly {{count}} strings in the same order as the images:
        {"descriptions":["...","..."]}
        """;

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
    // level          difficulty 1-5, turned into style instructions by StyleForLevel
    // previousLesson set on a re-teach: the explanation that did not work
    // missed         set on a re-teach or review: the questions the learner got wrong
    // isReview       true for the review lesson after a weak checkpoint result;
    //                SOURCE then covers several earlier lessons at once
    public static string Lesson(
        string topicTitle, string source, int level,
        string? previousLesson = null, IReadOnlyList<string>? missed = null, bool isReview = false)
    {
        var missedList = missed is { Count: > 0 } ? string.Join(" | ", missed) : "none recorded";

        // Extra instructions, only added when content is taught again.
        var reteach = "";
        if (isReview)
            reteach = $"""

                REVIEW: the learner scored below 60% on a checkpoint test of this material.
                SOURCE covers several earlier lessons. Re-teach only its most important ideas,
                concentrating on what the learner got wrong: {missedList}
                Write NEW quiz questions; do not repeat the ones listed above.
                """;
        else if (previousLesson is not null)
            reteach = $"""

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

            DIFFICULTY LEVEL: {{level}} of 5 (1 = easiest, 5 = hardest)
            TEACHING STYLE for this level (follow closely):
            {{StyleForLevel(level)}}
            {{reteach}}

            After the lesson, write {{QuizSize}} multiple-choice questions that can be answered
            from your lesson_text alone. {{QuestionRules}}

            Return JSON only, in exactly this shape:
            {"lesson_text":"the lesson",
             "example":"one concrete example or analogy",
             "insight":"one sentence: an extra interesting point from SOURCE that goes slightly beyond the lesson",
             "questions":[{{QuestionShape}}]}

            <<<SOURCE
            {{source}}
            SOURCE>>>
            """;
    }

    // ── Ask the tutor: answer a learner's question about the lesson on screen ─
    // The model has no memory between calls, so everything it needs is sent
    // again each time:
    //   lessonText  the explanation the learner is looking at (so "what do you
    //               mean by X?" can be answered about the actual wording);
    //   source      the same uploaded text the lesson was written from, which
    //               keeps the answer grounded in the learner's own material;
    //   history     the earlier questions and answers of this chat, so a
    //               follow-up such as "why?" makes sense;
    //   level       the same teaching style as the lesson (StyleForLevel).
    // The reply is JSON with one field, like every other prompt, so it goes
    // through the same parsing and error handling. Asking a question never
    // changes the difficulty level: that is decided only by quiz results.
    public static string Ask(
        string topicTitle, string source, int level, string lessonText,
        IReadOnlyList<(string Question, string Answer)> history, string question)
    {
        var earlier = history.Count == 0
            ? "none"
            : string.Join("\n", history.Select(h => $"Learner: {h.Question}\nTutor: {h.Answer}"));

        return $$"""
            {{AskTask}}
            You are a patient tutor. The learner has just read the LESSON below, part of the
            topic "{{topicTitle}}", and has a question about it before taking the quiz.

            Answer the QUESTION directly in at most 120 words.
            GROUNDING: use only facts found in SOURCE and LESSON. Do not add outside knowledge.
            If they do not cover the question, say that the uploaded material does not cover it
            and point the learner to the closest idea that it does cover.
            Do not write quiz questions and do not reveal quiz answers.

            DIFFICULTY LEVEL: {{level}} of 5 (1 = easiest, 5 = hardest)
            TEACHING STYLE for this level (follow its wording guidance; ignore its word count and quiz guidance):
            {{StyleForLevel(level)}}

            Return JSON only, in exactly this shape:
            {"answer":"..."}

            <<<SOURCE
            {{source}}
            SOURCE>>>

            LESSON:
            {{lessonText}}

            EARLIER IN THIS CHAT:
            {{earlier}}

            <<<QUESTION
            {{question}}
            QUESTION>>>
            """;
    }

    // ── Checkpoint test: questions over several completed lessons ─────────────
    // Only the questions on the CURRENT topic come from this prompt. The
    // "few questions from earlier topics" are picked from the database by C#
    // (questions the learner has already seen), so no LLM call is needed for them.
    public static string Checkpoint(string topicTitle, string source, int level, int count) => $$"""
        {{CheckpointTask}}
        You are writing a short checkpoint test for the topic "{{topicTitle}}".
        The learner has just finished several lessons based on SOURCE below.

        Write exactly {{count}} multiple-choice questions spread across the whole of SOURCE
        (not all on one part). Use only facts found in SOURCE.
        {{QuestionRules}}

        DIFFICULTY LEVEL: {{level}} of 5 (1 = easiest, 5 = hardest)
        STYLE for this level (use its guidance on quiz questions):
        {{StyleForLevel(level)}}

        Return JSON only, in exactly this shape:
        {"questions":[{{QuestionShape}}]}

        <<<SOURCE
        {{source}}
        SOURCE>>>
        """;
}
