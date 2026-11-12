namespace server.Models;

public class Document
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string Filename { get; set; } = "";
    public string FileType { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ProcessingStatus { get; set; } = "pending";
    public string UploadedAt { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string? DeletedAt { get; set; }
}
