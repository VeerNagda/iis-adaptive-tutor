using System.Net.Http.Json;
using System.Text.Json;

namespace server.LLM;

/// <summary>
/// Calls the Gemini REST API with a plain HttpClient.
/// Docs: https://ai.google.dev/api/generate-content
///
/// Config keys (appsettings):
///   GEMINI_API_KEY             - environment variable holding the secret key (see LlmKey; never committed)
///   GeminiAPI:Model            - the model tried first; defaults to gemini-3.8-flash
///   GeminiAPI:FallbackModels   - comma-separated models tried in order when the one before is not
///                                available (see "Model fallback" below).
///   GeminiAPI:AutoFallback     - true (default): after those, also try the suitable models Gemini lists
///                                for this key (see "Automatic fallback models").
///
/// Every prompt asks for JSON output via responseMimeType so the caller
/// receives a clean JSON string rather than markdown-wrapped text.
///
/// Transient HTTP errors (429, 500, 502, 503, 504) are retried up to 4 times
/// with exponential backoff (1 s, 2 s, 4 s, 8 s) on the same model before it counts as unavailable.
///
/// Model fallback: every Gemini model has its own limits (a free key allows only a few
/// requests per minute and per day for each model). When a model cannot answer, because its
/// quota is used up, it keeps returning 429 or 503, it does not answer in time, or the model
/// name does not exist, the same request goes to the next model in the list. A model that failed
/// is skipped for a while (SkipFor) so the next requests do not wait for it again.
/// A request that is wrong (400, for example a model that cannot read images) is NOT passed
/// on, because every model would refuse it the same way.
/// </summary>
public sealed class GeminiProvider(HttpClient http, IConfiguration config, ILogger<GeminiProvider> log)
    : ILlmProvider
{
    private readonly string _apiKey = LlmKey.Read(config)
        ?? throw new InvalidOperationException(
            "No Gemini API key. Set the GEMINI_API_KEY environment variable.");

    public bool IsMock => false;

    // The models in the order they are tried: the main one, then the fallbacks.
    private readonly List<string> _models = BuildModelList(config);

    // When a model failed, it is left out until this time. The provider is a singleton
    // shared by parallel calls, so every access takes the lock.
    private readonly Dictionary<string, DateTimeOffset> _skipUntil = [];
    private readonly object _skipLock = new();

    // Delays in milliseconds for each retry attempt (4 retries total).
    private static readonly int[] RetryDelaysMs = [1_000, 2_000, 4_000, 8_000];

    // ── Automatic fallback models ──────────────────────────────────────────────
    //
    // With GeminiAPI:AutoFallback on (the default) the provider asks Gemini once which
    // models this key can use and adds the suitable ones after the configured models.
    // Google's list does not say which models are free or what quota is left, so a
    // model is only found to be out of quota by trying it: it answers 429 at once and
    // is skipped for a while (see TryModelAsync). A key with billing turned on pays for
    // the models it falls back to, so switch AutoFallback off if that is not wanted.

    private List<string>? _autoModels;                       // null until discovery has worked
    private readonly SemaphoreSlim _discoveryLock = new(1);

    /// <summary>The configured models, then the discovered ones.</summary>
    private async Task<List<string>> AllModelsAsync(CancellationToken ct)
    {
        if (!config.GetValue("GeminiAPI:AutoFallback", true)) return _models;

        if (_autoModels is null)
        {
            await _discoveryLock.WaitAsync(ct);
            try { _autoModels ??= await DiscoverModelsAsync(ct); }
            finally { _discoveryLock.Release(); }
        }
        return _models.Concat(_autoModels ?? []).Distinct().ToList();
    }

    /// <summary>Most automatic fallbacks added, so a run of unavailable models stays short.</summary>
    private const int MaxAutoModels = 6;

    /// <summary>
    /// Lists the models the key can use and keeps the ones that fit this app: text
    /// models that read images and answer JSON ("flash" models, which includes
    /// "flash-lite"). Left out: Gemma (cannot read images, so a request would be refused
    /// rather than passed on), Pro (rarely free), the "latest" aliases, and the audio, image,
    /// live, embedding, omni and agent models. "lite" models come first because they usually have the larger
    /// daily allowance, then the newest version first. Returns null if the list could not be fetched.
    /// </summary>
    private async Task<List<string>?> DiscoverModelsAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000&key={_apiKey}";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));

            string[] notWanted =
                ["tts", "image", "audio", "live", "embed", "transcribe", "robotics", "computer-use", "research", "gemma", "pro", "learnlm", "aqa",
                 "omni",      // multimodal-output models, not text tutors
                 "latest"];   // aliases that may point at the model that is already out of quota

            var found = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var name = (m.GetProperty("name").GetString() ?? "").Replace("models/", "");
                bool canGenerate = m.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (canGenerate && name.Contains("flash") && !notWanted.Any(name.Contains))
                    found.Add(name);
            }

            var chosen = found
                .OrderByDescending(n => n.Contains("lite"))
                .ThenByDescending(n => n, StringComparer.Ordinal)
                .Where(n => !_models.Contains(n))
                .Take(MaxAutoModels)
                .ToList();
            log.LogInformation("Automatic fallback models: {Models}", chosen.Count == 0 ? "(none found)" : string.Join(", ", chosen));
            return chosen;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: only the configured models are used, and the list is asked for again next time.
            log.LogWarning("Could not list Gemini models, using only the configured ones: {Msg}", ex.Message);
            return null;
        }
    }

    private static List<string> BuildModelList(IConfiguration config)
    {
        var models = new List<string> { config["GeminiAPI:Model"] ?? "gemini-3.8-flash" };
        models.AddRange((config["GeminiAPI:FallbackModels"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return models.Distinct().ToList();
    }

    /// <summary>One model could not serve this request. The next model is tried.</summary>
    private sealed class ModelUnavailableException(string message, TimeSpan skipFor) : Exception(message)
    {
        public TimeSpan SkipFor { get; } = skipFor;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
        GenerateAsync(prompt, [], ct);

    public async Task<string> GenerateAsync(string prompt, IReadOnlyList<LlmImage> images, CancellationToken ct = default)
    {
        // A Gemini message is a list of "parts". A text-only prompt has one text
        // part. For multimodal input each image is added as its own part:
        // inline_data carries the image inside the request itself, as base64 text
        // (JSON cannot hold raw bytes) together with its MIME type. The model
        // reads the parts in order, so the prompt can say "the images, in order".
        var parts = new List<object> { new { text = prompt } };
        foreach (var image in images)
            parts.Add(new { inline_data = new { mime_type = image.MimeType, data = Convert.ToBase64String(image.Data) } });

        var requestBody = new
        {
            contents = new[] { new { parts } },
            // responseMimeType tells Gemini to return clean JSON without markdown fences.
            generationConfig = new { responseMimeType = "application/json" }
        };

        // Models that failed recently are left out. If that leaves none, all are tried
        // (better to try a skipped model than to fail without calling anything).
        var allModels = await AllModelsAsync(ct);
        List<string> candidates;
        lock (_skipLock)
        {
            var now = DateTimeOffset.UtcNow;
            candidates = allModels.Where(m => !_skipUntil.TryGetValue(m, out var until) || until <= now).ToList();
        }
        if (candidates.Count == 0) candidates = allModels;

        ModelUnavailableException? last = null;
        foreach (var model in candidates)
        {
            try
            {
                return await TryModelAsync(model, requestBody, images, ct);
            }
            catch (ModelUnavailableException ex)
            {
                last = ex;
                lock (_skipLock) _skipUntil[model] = DateTimeOffset.UtcNow + ex.SkipFor;
                if (model != candidates[^1])
                    log.LogWarning("Model {Model} is not available ({Why}). Trying the next model.", model, FirstLine(ex.Message));
            }
        }

        // Every model failed. The message of the last one is what the learner is told
        // (LlmFailure.Explain turns it into plain words).
        throw new LlmException(last!.Message);
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>
    /// Sends the request to one model. Returns the answer text, or throws
    /// ModelUnavailableException when this model cannot answer, so the caller can try the next one.
    /// </summary>
    private async Task<string> TryModelAsync(
        string model, object requestBody, IReadOnlyList<LlmImage> images, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";

        // Only used for the log line below: how long the call took and how much was sent.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double sentMb = images.Sum(i => i.Data.Length) / 1_000_000.0;

        // A call with no pictures should answer within seconds, so one that is silent
        // for 2 minutes is stuck and is better asked again. Calls with pictures
        // legitimately take minutes. The HttpClient's own 10 minute limit is the last resort.
        var timeout = images.Count == 0 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(8);
        int timeouts = 0;

        for (int attempt = 0; ; attempt++)
        {
            using var attemptTimer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptTimer.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(url, requestBody, attemptTimer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our timer ran out (the caller did not cancel): Gemini never answered.
                // Asked once more, then this model is given up on, so a stuck service
                // cannot hold the app for long.
                if (++timeouts <= 1)
                {
                    log.LogWarning("{Model} did not answer within {Min} minutes, asking again.", model, timeout.TotalMinutes);
                    continue;
                }
                throw new ModelUnavailableException(
                    $"Gemini did not answer within {timeout.TotalMinutes:0} minutes (twice). The service may be overloaded. Please retry in a few minutes.",
                    TimeSpan.FromMinutes(2));
            }
            using var _ = response;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;
                string message = $"Gemini {status}: {errorBody}";

                // RESOURCE_EXHAUSTED = a quota is used up. A daily quota ("PerDay" in the
                // error) will not come back for hours, so this model is skipped for an hour.
                // A per-minute quota comes back within a minute.
                if (errorBody.Contains("RESOURCE_EXHAUSTED"))
                    throw new ModelUnavailableException(message,
                        errorBody.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1));

                // The model name does not exist (or is not allowed for this key).
                if (status == 404)
                    throw new ModelUnavailableException($"Model '{model}' was not found. {message}", TimeSpan.FromHours(1));

                // Retry on transient server errors and per-minute rate limits only.
                bool transient = status is 429 or 500 or 502 or 503 or 504;
                if (transient && attempt < RetryDelaysMs.Length)
                {
                    log.LogWarning(
                        "{Model}: Gemini {Status} (attempt {A}/{Max}), retrying in {D} s…",
                        model, status, attempt + 1, RetryDelaysMs.Length, RetryDelaysMs[attempt] / 1000);
                    await Task.Delay(RetryDelaysMs[attempt], ct);
                    continue;
                }

                // Still failing after the retries: let the next model have a go.
                if (transient)
                    throw new ModelUnavailableException(message, TimeSpan.FromMinutes(2));

                // Any other error (400 and so on) is a problem with the request itself.
                throw new LlmException(message);
            }

            var raw = await response.Content.ReadAsStringAsync(ct);

            // Response shape:
            // { "candidates": [ { "content": { "parts": [ { "text": "<json>" } ] } } ] }
            try
            {
                using var doc = JsonDocument.Parse(raw);

                // Shows why a call was slow: a large upload, or a large thinking
                // token count (the model reasoning before it answers).
                var usage = doc.RootElement.TryGetProperty("usageMetadata", out var u) ? u : default;
                int Tokens(string name) =>
                    usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
                log.LogInformation(
                    "{Model} answered in {Sec:F0} s: {Images} images ({Mb:F1} MB sent), tokens: prompt {P}, thinking {T}, answer {A}.",
                    model, timer.Elapsed.TotalSeconds, images.Count, sentMb,
                    Tokens("promptTokenCount"), Tokens("thoughtsTokenCount"), Tokens("candidatesTokenCount"));

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
