namespace server.LLM;

/// <summary>
/// Returns hardcoded JSON so the app runs with no Gemini key.
/// Used when LlmProvider=Mock in config (the default).
///
/// Each method that calls the LLM passes a prompt containing a recognisable
/// keyword (e.g. "SECTION_SPLIT", "TEACH", "QUIZ") so the mock can return
/// the right canned shape. Those keywords are defined alongside each prompt
/// in the service classes (Steps 2–5); the placeholders below will be
/// replaced when those steps are implemented.
/// </summary>
public sealed class MockLlmProvider : ILlmProvider
{
    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        // Dispatch on a marker embedded in the prompt by the calling service.
        if (prompt.Contains("SECTION_SPLIT"))
            return Task.FromResult(MockSections());

        if (prompt.Contains("TEACH"))
            return Task.FromResult(MockTeach(prompt));

        if (prompt.Contains("QUIZ"))
            return Task.FromResult(MockQuiz());

        // Fallback — should not be reached once all callers embed a marker.
        return Task.FromResult("""{"error":"MockLlmProvider: unrecognised prompt marker."}""");
    }

    // ── Canned responses (shapes must match what the service classes expect) ──

    private static string MockSections() => """
        {
          "sections": [
            {
              "title": "Introduction",
              "text": "This is the first section of the uploaded material. It introduces the core topic and sets the context for what follows."
            },
            {
              "title": "Key Concepts",
              "text": "This section covers the main ideas. Each concept builds on the previous one to form a coherent understanding."
            },
            {
              "title": "Applications",
              "text": "Here we look at how the concepts are applied in practice, with concrete examples drawn from the material."
            },
            {
              "title": "Summary",
              "text": "A recap of the main points covered, highlighting what a learner should take away from this material."
            }
          ]
        }
        """;

    private static string MockTeach(string prompt)
    {
        // Extract a rough section title from the prompt if present, else use generic.
        var title = prompt.Contains("title:") ? "this section" : "this section";
        return $$"""
        {
          "content": "This is a mock explanation for {{title}}. In a real session the tutor would give a ~150-word explanation drawn from the uploaded material, followed by a concrete example. The simplified re-teach version would use an analogy and shorter sentences, focusing on the concept that was missed."
        }
        """;
    }

    private static string MockQuiz() => """
        {
          "questions": [
            {
              "text": "Which of the following best describes the main idea of this section?",
              "options": [
                { "id": "a", "text": "Option A — a plausible but incorrect answer." },
                { "id": "b", "text": "Option B — the correct answer." },
                { "id": "c", "text": "Option C — a common misconception." },
                { "id": "d", "text": "Option D — an unrelated distractor." }
              ],
              "correctAnswer": "b",
              "explanation": "Option B is correct because it accurately captures the central idea described in the section.",
              "concept": "Core concept"
            },
            {
              "text": "What is the purpose of the example given in this section?",
              "options": [
                { "id": "a", "text": "To confuse the reader." },
                { "id": "b", "text": "To illustrate an unrelated point." },
                { "id": "c", "text": "To make the abstract idea concrete and memorable." },
                { "id": "d", "text": "To summarise the entire document." }
              ],
              "correctAnswer": "c",
              "explanation": "Examples exist to anchor abstract ideas to something familiar.",
              "concept": "Learning strategy"
            },
            {
              "text": "Which term is most closely associated with the topic of this section?",
              "options": [
                { "id": "a", "text": "Term A" },
                { "id": "b", "text": "Term B" },
                { "id": "c", "text": "Term C (correct)" },
                { "id": "d", "text": "Term D" }
              ],
              "correctAnswer": "c",
              "explanation": "Term C is defined directly in the section text.",
              "concept": "Terminology"
            },
            {
              "text": "What would happen if the key concept of this section were misunderstood?",
              "options": [
                { "id": "a", "text": "Nothing — it has no downstream effect." },
                { "id": "b", "text": "Later sections that build on it would be harder to follow." },
                { "id": "c", "text": "The document would need to be rewritten." },
                { "id": "d", "text": "Only the summary would be affected." }
              ],
              "correctAnswer": "b",
              "explanation": "Concepts in this material build on each other, so a gap here creates confusion later.",
              "concept": "Dependencies"
            }
          ]
        }
        """;
}
