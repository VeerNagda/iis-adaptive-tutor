using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.Models;
using server.Services;

namespace server.Controllers;

[ApiController]
[Route("courses/{courseId:int}/documents")]
public class DocumentsController(IDbConnectionFactory db, IConfiguration config, IngestionService ingestion) : ControllerBase
{
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

        using var conn = db.CreateConnection();
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM courses WHERE id = $id;";
        existsCmd.Parameters.AddWithValue("$id", courseId);
        if ((long)(existsCmd.ExecuteScalar() ?? 0L) == 0)
            return NotFound("Course not found.");

        // An unsupported file is refused with the reason and what to do instead
        // (for example a standalone picture, a video or a ZIP file).
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Capability.AcceptedExtensions.Contains(ext))
        {
            var reason = Capability.RejectReason(ext);
            Capability.Log(conn, courseId, Capability.UploadRejected, reason, file.FileName);
            return BadRequest(reason);
        }

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

    /// <summary>The document's processing status, or null if it does not exist in this course.</summary>
    private string? Status(int courseId, int docId)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT processing_status FROM documents
            WHERE id = $docId AND course_id = $courseId AND deleted_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$docId", docId);
        cmd.Parameters.AddWithValue("$courseId", courseId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Scope check before ingestion: pages, words, estimated chunks and any
    /// warnings (textbook-sized file, scan with no text). No LLM call.
    /// </summary>
    [HttpGet("{docId:int}/scope")]
    public IActionResult Scope(int courseId, int docId)
    {
        if (Status(courseId, docId) is null) return NotFound("Document not found.");
        try
        {
            return Ok(ingestion.CheckScope(docId));
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"The file could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Ingests one uploaded document now (extract text, chunk, sections, embeddings).
    /// Ingestion is started by hand from the UI instead of a background poller, so
    /// LLM quota is only spent when the user asks for it. Once a document is
    /// ingested, its topics are added the next time the topics list is loaded.
    /// </summary>
    [HttpPost("{docId:int}/ingest")]
    public async Task<IActionResult> Ingest(int courseId, int docId, [FromBody] IngestRequest? request, CancellationToken ct)
    {
        var status = Status(courseId, docId);
        if (status is null) return NotFound("Document not found.");
        if (status == "completed") return NoContent();   // nothing to do

        // The scope check is enforced here, not only in the browser: a file with a
        // warning is not ingested until the learner has said what to do with it.
        int? fromPage = null, toPage = null;
        try
        {
            var scope = ingestion.CheckScope(docId);
            string? choiceText = request?.Choice switch
            {
                "range" => $"Ingest a page range ({request.FromPage}-{request.ToPage})",
                "first" => $"Ingest first {request.ToPage} {scope.Unit}",
                "continue" => "Continue anyway",
                _ => null,
            };
            if (scope.Warnings.Count > 0 && choiceText is null)
                return BadRequest("Please choose how much of this file to ingest.");

            if (request?.Choice is "range" or "first")
            {
                fromPage = request.Choice == "first" ? 1 : request.FromPage;
                toPage = request.ToPage;
                if (fromPage is null || toPage is null || fromPage < 1 || toPage > scope.Pages || fromPage > toPage)
                    return BadRequest($"Please pick {scope.Unit} between 1 and {scope.Pages}.");
            }

            if (scope.Warnings.Count > 0)
            {
                using var conn = db.CreateConnection();
                Capability.Log(conn, courseId, Capability.ScopeWarning, string.Join(" ", scope.Warnings), choiceText);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(502, $"The file could not be read: {ex.Message}");
        }

        try
        {
            await ingestion.IngestAsync(docId, fromPage, toPage, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            // IngestAsync has already stored the failure on the document row.
            var message = ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message;
            return StatusCode(502, $"Ingestion failed: {message}");
        }
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
