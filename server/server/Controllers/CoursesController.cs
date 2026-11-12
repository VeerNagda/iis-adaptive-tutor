using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.Models;

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
