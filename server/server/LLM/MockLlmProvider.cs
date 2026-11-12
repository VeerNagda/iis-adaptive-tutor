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
}
