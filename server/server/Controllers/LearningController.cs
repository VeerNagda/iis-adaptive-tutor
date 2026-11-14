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
    public Task<IActionResult> StartSession(int courseId, int topicId) =>
        Run(() =>
        {
            var sessionId = learning.StartSession(courseId, topicId);
            return Task.FromResult<IActionResult>(
                sessionId is null ? NotFound("Topic not found.") : Ok(new { id = sessionId }));
        });

    /// <summary>Start (or resume) the mandatory test over the recently completed topics.</summary>
    [HttpPost("topic-test")]
    public Task<IActionResult> StartTopicTest(int courseId) =>
        Run(() => Task.FromResult<IActionResult>(Ok(new { id = learning.StartTopicTest(courseId) })));

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

    /// <summary>"Ask the tutor": answer a question about the lesson on screen.</summary>
    [HttpPost("sessions/{sessionId:int}/ask")]
    public Task<IActionResult> Ask(int courseId, int sessionId, [FromBody] AskRequest request, CancellationToken ct) =>
        Run(async () => Ok(await learning.AskQuestionAsync(courseId, sessionId, request, ct)));

    /// <summary>"Skip / I know this": move on without the quiz.</summary>
    [HttpPost("sessions/{sessionId:int}/skip")]
    public Task<IActionResult> Skip(int courseId, int sessionId) =>
        Run(() => { learning.Skip(courseId, sessionId); return Done(); });

    /// <summary>Submit quiz answers; returns the score, per-question feedback and the decision.</summary>
    [HttpPost("sessions/{sessionId:int}/quizzes/{quizId:int}/answers")]
    public Task<IActionResult> SubmitAnswers(int courseId, int sessionId, int quizId, [FromBody] SubmitAnswersRequest request) =>
        Run(() => Task.FromResult<IActionResult>(Ok(learning.SubmitAnswers(courseId, sessionId, quizId, request))));

    // ── On the feedback screen, before the decision takes effect ──────────────

    /// <summary>"That was a slip": answer one wrong question again (once per quiz).</summary>
    [HttpPost("sessions/{sessionId:int}/quizzes/{quizId:int}/questions/{questionId:int}/slip")]
    public Task<IActionResult> Slip(int courseId, int sessionId, int quizId, int questionId, [FromBody] SlipRequest request) =>
        Run(() => Task.FromResult<IActionResult>(Ok(learning.MarkSlip(courseId, sessionId, quizId, questionId, request.Answer))));

    /// <summary>"Report a wrong question": take it out of the score.</summary>
    [HttpPost("sessions/{sessionId:int}/quizzes/{quizId:int}/questions/{questionId:int}/flag")]
    public Task<IActionResult> Flag(int courseId, int sessionId, int quizId, int questionId) =>
        Run(() => Task.FromResult<IActionResult>(Ok(learning.FlagQuestion(courseId, sessionId, quizId, questionId))));

    /// <summary>"Next": apply the decision, with the level changes the learner chose to keep.</summary>
    [HttpPost("sessions/{sessionId:int}/quizzes/{quizId:int}/finish")]
    public Task<IActionResult> Finish(int courseId, int sessionId, int quizId, [FromBody] FinishQuizRequest request) =>
        Run(() => { learning.FinishQuiz(courseId, sessionId, quizId, request); return Done(); });

    // ── The learner's controls over what the tutor has stored ─────────────────

    /// <summary>"Reset level" of one topic.</summary>
    [HttpPost("topics/{topicId:int}/reset-level")]
    public Task<IActionResult> ResetLevel(int courseId, int topicId) =>
        Run(() => { learning.ResetLevel(courseId, topicId); return Done(); });

    /// <summary>"Pause adaptation" on or off.</summary>
    [HttpPost("adaptation")]
    public Task<IActionResult> PauseAdaptation(int courseId, [FromBody] PauseRequest request) =>
        Run(() => { learning.SetAdaptationPaused(courseId, request.Paused); return Done(); });

    /// <summary>Past sessions and per-topic progress and difficulty.</summary>
    [HttpGet("history")]
    public IActionResult GetHistory(int courseId)
    {
        var history = learning.GetHistory(courseId);
        history.MockMode = learning.MockMode;   // so the History page shows the MOCK MODE badge too
        return Ok(history);
    }

    private Task<IActionResult> Done() => Task.FromResult<IActionResult>(NoContent());

    /// <summary>
    /// Runs an action and maps LLM failures to 502 with a plain message
    /// (LlmFailure.Explain), so the frontend can show it next to a "Retry" button.
    /// A failed call is reported as failed; nothing falls back to mock content.
    /// </summary>
    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (LlmException ex)
        {
            return StatusCode(502, LlmFailure.Explain(ex));
        }
        catch (LearningException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is OperationCanceledException && !HttpContext.RequestAborted.IsCancellationRequested))
        {
            // Network failure, or the AI service timed out (not the browser leaving).
            return StatusCode(502, LlmFailure.Explain(ex));
        }
    }
}
