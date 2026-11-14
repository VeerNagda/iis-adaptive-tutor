namespace server.LLM;

/// <summary>
/// Single point of contact for all LLM calls.
/// Every call is "prompt in → JSON text out"; callers build the prompt
/// (see <see cref="Prompts"/>) and parse the returned string into their own types.
/// Throw <see cref="LlmException"/> on failure so callers can surface it cleanly.
/// </summary>
public interface ILlmProvider
{
    /// <summary>True for the canned mock, so the UI can show a "MOCK MODE" badge.</summary>
    bool IsMock { get; }

    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Same as above, but the model is also shown images (multimodal input).
    /// Used during ingestion to turn the pictures in a document into text.
    /// </summary>
    Task<string> GenerateAsync(string prompt, IReadOnlyList<LlmImage> images, CancellationToken ct = default);
}

/// <summary>One image sent to the LLM: the file's bytes and its type, e.g. "image/png".</summary>
public sealed record LlmImage(byte[] Data, string MimeType);

/// <summary>Thrown when the LLM call fails (network, API error, bad response shape).</summary>
public sealed class LlmException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Cleans an LLM reply before it is parsed as JSON. Even when asked for JSON
/// only, a model sometimes wraps the object in ```json fences or adds stray
/// characters before or after it. Every reply in this app is one JSON object,
/// so everything outside the first '{' and the last '}' is dropped.
/// </summary>
public static class LlmJson
{
    public static string Extract(string reply)
    {
        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : reply;
    }
}

/// <summary>
/// Finds the Gemini API key. The key is never stored in source control:
///   1. environment variable GEMINI_API_KEY (preferred), or
///   2. config value GeminiAPI:APIKEY (dotnet user-secrets, or the git-ignored
///      appsettings.Development.json).
/// Returns null when no key is set; Program.cs then falls back to the mock provider.
/// </summary>
public static class LlmKey
{
    public static string? Read(IConfiguration config)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) key = config["GeminiAPI:APIKEY"];
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
