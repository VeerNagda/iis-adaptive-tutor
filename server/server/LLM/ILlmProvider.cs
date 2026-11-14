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
/// Turns a failed LLM call into one plain sentence for the learner.
///
/// The providers throw the AI service's raw reply (useful in the server log,
/// unreadable on screen). At the edge of the API that raw error is replaced by
/// one of the fixed messages below, each saying what happened and what to do.
/// A failure is always shown as a failure: the app never switches to mock
/// content by itself, because canned text passed off as generated would be
/// worse than an honest "it did not work".
/// </summary>
public static class LlmFailure
{
    public const string Quota =
        "The AI service's quota is used up for now, so nothing new could be made. "
        + "Nothing was changed. Please try again later.";
    public const string TooSlow =
        "The AI service took too long to answer. Nothing was changed. Please try again.";
    public const string Unreachable =
        "The AI service could not be reached. Check your internet connection and try again.";
    public const string KeyRejected =
        "The AI service did not accept the API key. Check the GEMINI_API_KEY setting, then try again.";
    public const string ServiceError =
        "The AI service returned an error. Nothing was changed. Please try again in a moment.";

    public static string Explain(Exception ex)
    {
        string raw = ex.Message;
        return ex switch
        {
            // A timeout surfaces as a cancelled task.
            OperationCanceledException => TooSlow,
            HttpRequestException => Unreachable,
            _ when raw.Contains("RESOURCE_EXHAUSTED") || raw.Contains(" 429") => Quota,
            _ when raw.Contains("API_KEY_INVALID") || raw.Contains(" 401") || raw.Contains(" 403") => KeyRejected,
            // "Gemini 503: {...}" and similar: the raw reply of the service.
            _ when raw.StartsWith("Gemini ") || raw.StartsWith("Could not parse Gemini") || raw.StartsWith("Could not parse embed") => ServiceError,
            // Anything else was already written for the learner (for example "The AI returned an incomplete lesson. Please retry.").
            _ => raw,
        };
    }
}

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
