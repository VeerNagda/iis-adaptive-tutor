namespace server.LLM;

/// <summary>
/// Produces embedding vectors for text strings.
/// Two implementations are registered via config (same LlmProvider switch as ILlmProvider):
///   GeminiEmbedder  — calls Gemini batchEmbedContents; needs a real API key.
///   MockGeminiEmbedder — returns zero vectors so the app works without a key.
/// </summary>
public interface IGeminiEmbedder
{
    /// <param name="texts">Strings to embed.</param>
    /// <param name="taskType">
    ///   "RETRIEVAL_DOCUMENT" for chunks written to the index.
    ///   "RETRIEVAL_QUERY"    for user queries at search time.
    ///   The correct task type improves recall accuracy.
    /// </param>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        string taskType = "RETRIEVAL_DOCUMENT",
        CancellationToken ct = default);
}

/// <summary>Returns 768-dim zero vectors so ingestion runs without a Gemini key.
/// (text-embedding-005 also outputs 768 dims by default)</summary>
public sealed class MockGeminiEmbedder : IGeminiEmbedder
{
    private const int Dims = 768;

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        string taskType = "RETRIEVAL_DOCUMENT",
        CancellationToken ct = default)
    {
        IReadOnlyList<float[]> result = texts.Select(_ => new float[Dims]).ToArray();
        return Task.FromResult(result);
    }
}
