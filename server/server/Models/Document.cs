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

/// <summary>
/// What a file contains, worked out before any LLM call, so the learner can be
/// warned when it is not the kind of material the tutor handles well.
/// </summary>
public class ScopeReport
{
    /// <summary>"pages" or "slides", for the wording of messages.</summary>
    public string Unit { get; set; } = "pages";
    public int Pages { get; set; }
    public int PagesWithoutText { get; set; }
    public int Words { get; set; }
    public int EstimatedChunks { get; set; }
    /// <summary>Default for "Ingest first N pages".</summary>
    public int SuggestedFirstPages { get; set; }
    /// <summary>Empty when the file is fine. Otherwise the learner must choose what to do.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>How much of a file to ingest. Sent with "Ingest" after a scope warning.</summary>
public class IngestRequest
{
    /// <summary>"range" (FromPage..ToPage), "first" (pages 1..ToPage) or "continue" (everything).</summary>
    public string? Choice { get; set; }
    public int? FromPage { get; set; }
    public int? ToPage { get; set; }
}
