using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.Models;
using server.Services;

namespace server.Controllers;

[ApiController]
[Route("courses")]
public class CoursesController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, description, created_at FROM courses ORDER BY created_at DESC;";

        var courses = new List<Course>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            courses.Add(new Course
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = reader.GetString(3),
            });
        }

        return Ok(courses);
    }

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, description, created_at FROM courses WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return NotFound();

        return Ok(new Course
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = reader.GetString(3),
        });
    }

    /// <summary>
    /// What the tutor can and cannot do, and which files it accepts. The text comes
    /// from the constants in <see cref="Capability"/> so every page shows the same words.
    /// </summary>
    [HttpGet("/capabilities")]
    public IActionResult Capabilities() => Ok(new
    {
        can = Capability.Can,
        cannot = Capability.Cannot,
        acceptedExtensions = Capability.AcceptedExtensions,
        rejectReasons = Capability.RejectReasons,
        otherFileReason = Capability.OtherFileReason,
    });

    /// <summary>
    /// Lets the browser record a choice the server would otherwise never see
    /// (for example "Cancel" on the scope popup). Only listed event types are accepted.
    /// </summary>
    [HttpPost("{id:int}/events")]
    public IActionResult LogEvent(int id, [FromBody] ClientEvent e)
    {
        if (!Capability.ClientEvents.Contains(e.EventType)) return BadRequest("Unknown event type.");
        using var conn = db.CreateConnection();
        Capability.Log(conn, id, e.EventType, e.ShownMessage, e.UserChoice);
        return NoContent();
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateCourseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest("Title is required.");

        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO courses (title, description)
            VALUES ($title, $description)
            RETURNING id, title, description, created_at;
            """;
        cmd.Parameters.AddWithValue("$title", request.Title.Trim());
        cmd.Parameters.AddWithValue("$description", (object?)request.Description?.Trim() ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        reader.Read();

        return CreatedAtAction(nameof(GetById), new { id = reader.GetInt32(0) }, new Course
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = reader.GetString(3),
        });
    }
}
