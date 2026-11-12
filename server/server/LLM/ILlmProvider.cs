namespace server.LLM;

/// <summary>
/// Single point of contact for all LLM calls.
/// Every call is "prompt in → JSON text out"; callers build the prompt
/// and parse the returned string into their own types.
/// Throw <see cref="LlmException"/> on failure so callers can surface it cleanly.
/// </summary>
public interface ILlmProvider
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}

/// <summary>Thrown when the LLM call fails (network, API error, bad response shape).</summary>
public sealed class LlmException(string message, Exception? inner = null)
    : Exception(message, inner);
