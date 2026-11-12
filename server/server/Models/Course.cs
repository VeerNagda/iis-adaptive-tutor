namespace server.Models;

public class Course
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string CreatedAt { get; set; } = "";
}

public class CreateCourseRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
}
