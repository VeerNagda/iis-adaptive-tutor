namespace server.Models;

public class Course
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>A capability event reported by the browser (see Capability.ClientEvents).</summary>
public class ClientEvent
{
    public string EventType { get; set; } = "";
    public string ShownMessage { get; set; } = "";
    public string? UserChoice { get; set; }
}

public class CreateCourseRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
}
