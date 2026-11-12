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
}

/// <summary>Thrown when the LLM call fails (network, API error, bad response shape).</summary>
public sealed class LlmException(string message, Exception? inner = null)
    : Exception(message, inner);

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
