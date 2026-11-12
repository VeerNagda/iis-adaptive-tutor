using System.Text.RegularExpressions;
using server.Data;
using server.LLM;

namespace server.Services;

/// <summary>
/// Polls the database every 30 seconds for documents with processing_status = 'pending'
/// or 'failed' and runs IngestionService on each one.
///
/// When the Gemini daily quota is exhausted the service pauses itself for the duration
/// suggested in the API response (retryDelay field) and logs a clear message.
/// </summary>
public sealed class IngestionBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<IngestionBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // Set when a daily quota error is returned; processing is skipped until this time.
    private DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Ingestion background service started (poll every {Interval}s).",
            PollInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow < _pauseUntil)
            {
                var remaining = _pauseUntil - DateTimeOffset.UtcNow;
                log.LogInformation(
                    "Gemini daily quota exhausted — ingestion paused for another {H}h {M}m.",
                    (int)remaining.TotalHours, remaining.Minutes);
            }
            else
            {
                try
                {
                    await ProcessPendingAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogError(ex, "Unhandled error in ingestion poll cycle.");
                }
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int? docId = GetNextPendingId();
            if (docId is null) return;

            log.LogInformation("Found pending document {Id}, starting ingestion.", docId);

            await using var scope = scopeFactory.CreateAsyncScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();

            try
            {
                await ingestion.IngestAsync(docId.Value, ct);
            }
            catch (LlmException ex) when (ex.Message.Contains("RESOURCE_EXHAUSTED"))
            {
                // Daily quota exhausted — parse the suggested retry delay from the response.
                var delay = ParseRetryDelay(ex.Message);
                _pauseUntil = DateTimeOffset.UtcNow.Add(delay);
                log.LogWarning(
                    "Gemini daily quota exceeded. Ingestion paused until {Until} ({H}h {M}m from now).",
                    _pauseUntil.ToLocalTime(), (int)delay.TotalHours, delay.Minutes);
                return;
            }
        }
    }

    private int? GetNextPendingId()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id FROM documents WHERE processing_status IN ('pending', 'failed') LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is null ? null : Convert.ToInt32(result);
    }

    // Extract the retryDelay value (seconds) from the Gemini error JSON.
    // Falls back to 1 hour if the field is missing or unparseable.
    private static TimeSpan ParseRetryDelay(string message)
    {
        var match = Regex.Match(message, @"""retryDelay""\s*:\s*""(\d+)s""");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int seconds))
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromHours(1);
    }
}
