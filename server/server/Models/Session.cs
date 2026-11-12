namespace server.Models;

public class Session
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public int? CurrentTopicId { get; set; }
    public int? CurrentConceptId { get; set; }
    public int CurrentLevel { get; set; }
    public string Status { get; set; } = "active";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
}
