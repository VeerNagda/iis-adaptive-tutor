using Microsoft.AspNetCore.Mvc;
using server.LLM;
using server.Models;
using server.Services;

namespace server.Controllers;

/// <summary>
/// HTTP endpoints for "Start Learning". The controller stays thin: it calls
/// LearningService and translates failures into status codes.
/// </summary>
[ApiController]
[Route("courses/{courseId:int}")]
public class LearningController(LearningService learning) : ControllerBase
{
    /// <summary>Topics of the course (extracted by the LLM on first call, then cached).</summary>
    [HttpGet("topics")]
    public Task<IActionResult> GetTopics(int courseId, CancellationToken ct) =>
        Run(async () => Ok(await learning.GetTopicsAsync(courseId, ct)));

    /// <summary>Start (or resume) a learning session on one topic.</summary>
    [HttpPost("topics/{topicId:int}/sessions")]
    public IActionResult StartSession(int courseId, int topicId)
    {
        var sessionId = learning.StartSession(courseId, topicId);
        return sessionId is null ? NotFound("Topic not found.") : Ok(new { id = sessionId });
    }

    // ── Lesson loop: /courses/{id}/sessions/{sessionId}/... ───────────────────

    /// <summary>What to show now: a lesson, a quiz, or the end screen.</summary>
    [HttpGet("sessions/{sessionId:int}/step")]
    public Task<IActionResult> GetStep(int courseId, int sessionId, CancellationToken ct) =>
        Run(async () =>
        {
            var step = await learning.GetStepAsync(courseId, sessionId, ct);
            return step is null ? NotFound("Session not found.") : Ok(step);
        });

    /// <summary>Lesson read: go to its quiz.</summary>
    [HttpPost("sessions/{sessionId:int}/continue")]
    public Task<IActionResult> Continue(int courseId, int sessionId) =>
        Run(() => { learning.Continue(courseId, sessionId); return Done(); });

    /// <summary>"Explain differently": a fresh explanation of the same step.</summary>
    [HttpPost("sessions/{sessionId:int}/explain")]
    public Task<IActionResult> Explain(int courseId, int sessionId) =>
        Run(() => { learning.ExplainDifferently(courseId, sessionId); return Done(); });

    /// <summary>"Skip / I know this": move on without the quiz.</summary>
    [HttpPost("sessions/{sessionId:int}/skip")]
    public Task<IActionResult> Skip(int courseId, int sessionId) =>
        Run(() => { learning.Skip(courseId, sessionId); return Done(); });

    /// <summary>Submit quiz answers; returns the score, per-question feedback and the decision.</summary>
    [HttpPost("sessions/{sessionId:int}/quizzes/{quizId:int}/answers")]
    public Task<IActionResult> SubmitAnswers(int courseId, int sessionId, int quizId, [FromBody] SubmitAnswersRequest request) =>
        Run(() => Task.FromResult<IActionResult>(Ok(learning.SubmitAnswers(courseId, sessionId, quizId, request))));

    /// <summary>Past sessions and per-topic progress and difficulty.</summary>
    [HttpGet("history")]
    public IActionResult GetHistory(int courseId) => Ok(learning.GetHistory(courseId));

    private Task<IActionResult> Done() => Task.FromResult<IActionResult>(NoContent());

    /// <summary>
    /// Runs an action and maps LLM failures to 502 with a readable message,
    /// so the frontend can show it next to a "Retry" button.
    /// </summary>
    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (LlmException ex)
        {
            return StatusCode(502, ex.Message);
        }
        catch (LearningException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, "Could not reach the AI service. Check your connection and retry.");
        }
    }
}
