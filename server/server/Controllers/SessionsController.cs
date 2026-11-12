using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.Models;

namespace server.Controllers;

[ApiController]
[Route("courses/{courseId:int}/sessions")]
public class SessionsController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll(int courseId)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, course_id, current_topic_id, current_concept_id, current_level, status, started_at, ended_at
            FROM sessions
            WHERE course_id = $courseId
            ORDER BY started_at DESC;
            """;
        cmd.Parameters.AddWithValue("$courseId", courseId);

        var sessions = new List<Session>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new Session
            {
                Id = reader.GetInt32(0),
                CourseId = reader.GetInt32(1),
                CurrentTopicId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                CurrentConceptId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                CurrentLevel = reader.GetInt32(4),
                Status = reader.GetString(5),
                StartedAt = reader.GetString(6),
                EndedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return Ok(sessions);
    }

    [HttpPost]
    public IActionResult Create(int courseId)
    {
        using var conn = db.CreateConnection();

        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM courses WHERE id = $id;";
        existsCmd.Parameters.AddWithValue("$id", courseId);
        if ((long)(existsCmd.ExecuteScalar() ?? 0L) == 0)
            return NotFound("Course not found.");

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO sessions (course_id)
            VALUES ($courseId)
            RETURNING id, course_id, current_topic_id, current_concept_id, current_level, status, started_at, ended_at;
            """;
        insertCmd.Parameters.AddWithValue("$courseId", courseId);

        using var reader = insertCmd.ExecuteReader();
        reader.Read();

        var session = new Session
        {
            Id = reader.GetInt32(0),
            CourseId = reader.GetInt32(1),
            CurrentTopicId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            CurrentConceptId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            CurrentLevel = reader.GetInt32(4),
            Status = reader.GetString(5),
            StartedAt = reader.GetString(6),
            EndedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
        };

        return CreatedAtAction(nameof(GetAll), new { courseId }, session);
    }
}
