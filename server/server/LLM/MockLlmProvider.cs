using System.Text.Json;
using System.Text.RegularExpressions;

namespace server.LLM;

/// <summary>
/// Stand-in for the real LLM, used when no API key is set (or LlmProvider=Mock).
/// It returns canned JSON in the same shapes the real model is asked for, so the
/// whole flow can be demoed offline. The content is clearly marked "(MOCK)".
///
/// How it knows what to return: every prompt in Prompts.cs starts with a task
/// marker such as [[TASK:TOPICS]]; the mock switches on that marker.
/// </summary>
public sealed class MockLlmProvider : ILlmProvider
{
    public bool IsMock => true;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        string json =
            prompt.Contains(Prompts.SectionsTask) ? MockSections(prompt) :
            prompt.Contains(Prompts.TopicsTask) ? MockTopics(prompt) :
            prompt.Contains(Prompts.LessonTask) ? MockLesson(prompt) :
            """{"error":"MockLlmProvider: unrecognised task marker."}""";

        return Task.FromResult(json);
    }

    // ── Sections: spread the chunks listed in the prompt over up to 4 sections ─
    private static string MockSections(string prompt)
    {
        int chunkCount = Regex.Matches(prompt, @"\[Chunk \d+\]").Count;
        int sectionCount = Math.Clamp(chunkCount, 1, 4);

        var sections = Enumerable.Range(0, sectionCount).Select(s => new
        {
            title = $"(MOCK) Section {s + 1}",
            summary = "Mock section created without an LLM. It groups neighbouring chunks of the document.",
            key_concepts = new[] { "Mock concept" },
            difficulty = 1,
            chunk_indexes = Enumerable.Range(0, chunkCount).Where(i => i % sectionCount == s).ToArray()
        });

        return JsonSerializer.Serialize(new { sections });
    }

    // ── Topics: reuse the section titles in the prompt, two sections per topic ─
    private static string MockTopics(string prompt)
    {
        // Lines look like: [S12] Title — summary
        var lines = Regex.Matches(prompt, @"^\s*\[S(\d+)\] (.+?) — (.*)$", RegexOptions.Multiline)
            .Select(m => new
            {
                Id = int.Parse(m.Groups[1].Value),
                Title = m.Groups[2].Value.Trim(),
                Summary = m.Groups[3].Value.Trim()
            })
            .ToList();

        var topics = lines.Chunk(2).Take(8).Select(pair => new
        {
            title = pair[0].Title,
            summary = "(MOCK) " + pair[0].Summary,
            section_ids = pair.Select(p => p.Id).ToArray()
        });

        return JsonSerializer.Serialize(new { topics });
    }

    // ── Lesson + quiz: quotes the start of the real source text so the mock is
    //    visibly tied to the uploaded material; the quiz questions are canned ──
    private static string MockLesson(string prompt)
    {
        var title = Regex.Match(prompt, "topic \"(.+?)\"").Groups[1].Value;
        bool reteach = prompt.Contains("RE-TEACH:");
        // Echo the level so the adaptive difficulty is visible even in mock mode.
        int level = int.Parse(Regex.Match(prompt, @"DIFFICULTY LEVEL: (\d)").Groups[1].Value);

        var lessonText =
            $"(MOCK lesson at level {level}: {Prompts.StyleLabel(level)}{(reteach ? ", explained again in a different way" : "")}) "
            + "A real LLM would explain this part of the topic here. "
            + "The uploaded material for this step begins: \"" + SourceExcerpt(prompt, 60) + "…\"";

        return JsonSerializer.Serialize(new
        {
            lesson_text = lessonText,
            example = "(MOCK example) A real LLM would give one concrete example or analogy here.",
            insight = "(MOCK insight) A real LLM would add one extra interesting point from the material.",
            questions = MockQuestions(title, Prompts.QuizSize)
        });
    }

    /// <summary>First few words of the text between the SOURCE markers of a prompt.</summary>
    private static string SourceExcerpt(string prompt, int words)
    {
        var source = Regex.Match(prompt, @"<<<SOURCE\s*(.*?)\s*SOURCE>>>", RegexOptions.Singleline).Groups[1].Value;
        return string.Join(" ", source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(words));
    }

    /// <summary>
    /// Canned questions. A random set number makes every quiz "new" (as a real
    /// re-quiz would be), and the correct option moves between questions.
    /// </summary>
    private static object[] MockQuestions(string title, int count)
    {
        int set = Random.Shared.Next(100, 999);
        return Enumerable.Range(0, count).Select(i =>
        {
            int correct = (i + 1) % 4;
            return (object)new
            {
                text = $"(MOCK question {i + 1}, set {set}) Which option is the correct one for \"{title}\"?",
                options = Enumerable.Range(0, 4)
                    .Select(o => o == correct ? "The correct answer" : $"A wrong answer ({(char)('A' + o)})")
                    .ToArray(),
                correct_index = correct,
                explanation = "(MOCK explanation) A real LLM would explain here why this option is right."
            };
        }).ToArray();
    }
}
