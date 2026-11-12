using System.Net.Http.Json;
using System.Text.Json;

namespace server.LLM;

/// <summary>
/// Calls the Gemini REST API with a plain HttpClient.
/// Docs: https://ai.google.dev/api/generate-content
///
/// Config keys (appsettings):
///   GeminiAPI:APIKEY  — secret key (keep out of source control; use appsettings.Development.json or env var)
///   GeminiAPI:Model   — model ID; defaults to gemini-3.8-flash (current general-purpose workhorse)
///
/// Every prompt asks for JSON output via responseMimeType so the caller
/// receives a clean JSON string rather than markdown-wrapped text.
///
/// Transient HTTP errors (429, 500, 502, 503, 504) are retried up to 4 times
/// with exponential backoff (1 s, 2 s, 4 s, 8 s) before throwing LlmException.
/// </summary>
public sealed class GeminiProvider(HttpClient http, IConfiguration config, ILogger<GeminiProvider> log)
    : ILlmProvider
{
    private readonly string _apiKey = config["GeminiAPI:APIKEY"]
        ?? throw new InvalidOperationException(
            "GeminiAPI:APIKEY is missing. Add it to appsettings.Development.json or set the env var.");

    private readonly string _model = config["GeminiAPI:Model"] ?? "gemini-3.8-flash";

    // Delays in milliseconds for each retry attempt (4 retries total).
    private static readonly int[] RetryDelaysMs = [1_000, 2_000, 4_000, 8_000];

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            // responseMimeType tells Gemini to return clean JSON without markdown fences.
            generationConfig = new { responseMimeType = "application/json" }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        for (int attempt = 0; ; attempt++)
        {
            using var response = await http.PostAsJsonAsync(url, requestBody, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;

                // RESOURCE_EXHAUSTED = daily quota used up; retrying in seconds won't help.
                // Throw immediately so the caller can back off for hours, not milliseconds.
                bool quotaExhausted = errorBody.Contains("RESOURCE_EXHAUSTED");

                // Retry on transient server errors and per-minute rate limits only.
                bool transient = status is 429 or 500 or 502 or 503 or 504;
                if (transient && !quotaExhausted && attempt < RetryDelaysMs.Length)
                {
                    log.LogWarning(
                        "Gemini {Status} (attempt {A}/{Max}), retrying in {D} s…",
                        status, attempt + 1, RetryDelaysMs.Length, RetryDelaysMs[attempt] / 1000);
                    await Task.Delay(RetryDelaysMs[attempt], ct);
                    continue;
                }

                throw new LlmException($"Gemini {status}: {errorBody}");
            }

            var raw = await response.Content.ReadAsStringAsync(ct);

            // Response shape:
            // { "candidates": [ { "content": { "parts": [ { "text": "<json>" } ] } } ] }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement
                           .GetProperty("candidates")[0]
                           .GetProperty("content")
                           .GetProperty("parts")[0]
                           .GetProperty("text")
                           .GetString()
                       ?? throw new LlmException("Gemini response had a null text field.");
            }
            catch (Exception ex) when (ex is not LlmException)
            {
                throw new LlmException($"Could not parse Gemini response: {ex.Message}", ex);
            }
        }
    }
}
