using System.Net.Http.Json;
using System.Text.Json;

namespace server.LLM;

/// <summary>
/// Calls the Gemini batchEmbedContents endpoint to produce embedding vectors.
///
/// API docs: https://ai.google.dev/api/embeddings#v1beta.models.batchEmbedContents
///
/// Request body:
///   { "requests": [ { "model": "...", "content": { "parts": [{ "text": "..." }] },
///                     "taskType": "RETRIEVAL_DOCUMENT" } ] }
///
/// Response body:
///   { "embeddings": [ { "values": [0.1, 0.2, ...] } ] }
///
/// Sent in batches of 100 (API limit per request).
/// The model text-embedding-004 produces 768-dimensional vectors.
/// </summary>
public sealed class GeminiEmbedder(HttpClient http, IConfiguration config, ILogger<GeminiEmbedder> log)
    : IGeminiEmbedder
{
    private readonly string _apiKey = config["GeminiAPI:APIKEY"]
        ?? throw new InvalidOperationException(
            "GeminiAPI:APIKEY is missing. Add it to appsettings.Development.json.");

    private const string EmbedModel = "models/gemini-embedding-001";
    private static readonly int[] RetryDelaysMs = [1_000, 2_000, 4_000, 8_000];

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        string taskType = "RETRIEVAL_DOCUMENT",
        CancellationToken ct = default)
    {
        var all = new List<float[]>(texts.Count);
        var url = $"https://generativelanguage.googleapis.com/v1beta/{EmbedModel}:batchEmbedContents?key={_apiKey}";

        for (int i = 0; i < texts.Count; i += 100)
        {
            var batch = texts.Skip(i).Take(100).ToList();
            var body = new
            {
                requests = batch.Select(t => new
                {
                    model = EmbedModel,
                    content = new { parts = new[] { new { text = t } } },
                    taskType
                }).ToArray()
            };

            for (int attempt = 0; ; attempt++)
            {
                using var response = await http.PostAsJsonAsync(url, body, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    var status = (int)response.StatusCode;
                    bool transient = status is 429 or 500 or 502 or 503 or 504;
                    if (transient && attempt < RetryDelaysMs.Length)
                    {
                        log.LogWarning("Gemini embed {Status} (attempt {A}/{Max}), retrying in {D} s…",
                            status, attempt + 1, RetryDelaysMs.Length, RetryDelaysMs[attempt] / 1000);
                        await Task.Delay(RetryDelaysMs[attempt], ct);
                        continue;
                    }
                    throw new LlmException($"Gemini embed HTTP {status}: {err}");
                }

                var raw = await response.Content.ReadAsStringAsync(ct);
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    foreach (var emb in doc.RootElement.GetProperty("embeddings").EnumerateArray())
                    {
                        all.Add(emb.GetProperty("values")
                                   .EnumerateArray()
                                   .Select(v => v.GetSingle())
                                   .ToArray());
                    }
                }
                catch (Exception ex) when (ex is not LlmException)
                {
                    throw new LlmException($"Could not parse embed response: {ex.Message}", ex);
                }
                break;
            }
        }

        return all;
    }
}
