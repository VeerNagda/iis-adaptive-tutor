using Microsoft.AspNetCore.Mvc;
using server.LLM;
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
        catch (HttpRequestException)
        {
            return StatusCode(502, "Could not reach the AI service. Check your connection and retry.");
        }
    }
}
