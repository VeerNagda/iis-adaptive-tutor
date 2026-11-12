using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.Models;

namespace server.Controllers;

[ApiController]
[Route("courses/{courseId:int}/documents")]
public class DocumentsController(IDbConnectionFactory db, IConfiguration config) : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = [".pdf", ".pptx", ".docx"];

    [HttpGet]
    public IActionResult GetAll(int courseId)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, course_id, filename, file_type, file_path,
                   processing_status, error_message, uploaded_at
            FROM documents
            WHERE course_id = $courseId AND deleted_at IS NULL
            ORDER BY uploaded_at DESC;
            """;
        cmd.Parameters.AddWithValue("$courseId", courseId);

        var docs = new List<Document>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            docs.Add(new Document
            {
                Id = reader.GetInt32(0),
                CourseId = reader.GetInt32(1),
                Filename = reader.GetString(2),
                FileType = reader.GetString(3),
                FilePath = reader.GetString(4),
                ProcessingStatus = reader.GetString(5),
                ErrorMessage = reader.IsDBNull(6) ? null : reader.GetString(6),
                UploadedAt = reader.GetString(7),
            });
        }

        return Ok(docs);
    }

    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Upload(int courseId, IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return BadRequest($"File type '{ext}' is not allowed. Use PDF, PPTX, or DOCX.");

        using var conn = db.CreateConnection();
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM courses WHERE id = $id;";
        existsCmd.Parameters.AddWithValue("$id", courseId);
        if ((long)(existsCmd.ExecuteScalar() ?? 0L) == 0)
            return NotFound("Course not found.");

        var uploadsRoot = config["Uploads:Path"] ?? Path.Combine(AppContext.BaseDirectory, "uploads");
        var courseDir = Path.Combine(uploadsRoot, courseId.ToString());
        Directory.CreateDirectory(courseDir);

        var safeFilename = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(courseDir, safeFilename);

        await using (var stream = System.IO.File.Create(filePath))
            await file.CopyToAsync(stream);

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO documents (course_id, filename, file_type, file_path)
            VALUES ($courseId, $filename, $fileType, $filePath)
            RETURNING id, course_id, filename, file_type, file_path, processing_status, uploaded_at;
            """;
        insertCmd.Parameters.AddWithValue("$courseId", courseId);
        insertCmd.Parameters.AddWithValue("$filename", file.FileName);
        insertCmd.Parameters.AddWithValue("$fileType", ext.TrimStart('.'));
        insertCmd.Parameters.AddWithValue("$filePath", filePath);

        using var reader = insertCmd.ExecuteReader();
        reader.Read();

        return Created($"/courses/{courseId}/documents/{reader.GetInt32(0)}", new Document
        {
            Id = reader.GetInt32(0),
            CourseId = reader.GetInt32(1),
            Filename = reader.GetString(2),
            FileType = reader.GetString(3),
            FilePath = reader.GetString(4),
            ProcessingStatus = reader.GetString(5),
            UploadedAt = reader.GetString(6),
        });
    }

    [HttpDelete("{docId:int}")]
    public IActionResult SoftDelete(int courseId, int docId)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE documents
            SET deleted_at = CURRENT_TIMESTAMP
            WHERE id = $docId AND course_id = $courseId AND deleted_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$docId", docId);
        cmd.Parameters.AddWithValue("$courseId", courseId);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0) return NotFound("Document not found or already removed.");

        return NoContent();
    }
}
