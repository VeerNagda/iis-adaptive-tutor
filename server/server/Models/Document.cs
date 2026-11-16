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
    /// <summary>What ingestion did and did not do, as sentences for the file card.</summary>
    public DocumentReport Report { get; set; } = new();
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

/// <summary>
/// What ingestion actually did with a file. Every number is counted by the C#
/// pipeline while it runs (pages read, images sent, descriptions received);
/// none of it is text written by the LLM. Stored as JSON in documents.ingest_report.
/// </summary>
public class IngestReport
{
    public string Unit { get; set; } = "pages";
    public int? FromPage { get; set; }
    public int? ToPage { get; set; }

    public int PagesTotal { get; set; }
    public int PagesOutsideRange { get; set; }
    /// <summary>Pages in range that ended up with text to teach from.</summary>
    public int PagesRead { get; set; }
    /// <summary>Pages in range with no text and no described picture.</summary>
    public int PagesNoText { get; set; }

    public int ImagesFound { get; set; }
    public int ImagesDescribed { get; set; }
    public int ImagesTooSmall { get; set; }
    public int ImagesTooLarge { get; set; }
    public int ImagesDuplicate { get; set; }
    public int ImagesOverLimit { get; set; }
    /// <summary>Sent to the LLM, which returned an empty description (it judged them decoration).</summary>
    public int ImagesDecoration { get; set; }
    /// <summary>Sent to the LLM, which returned no entry for them at all.</summary>
    public int ImagesNoReply { get; set; }
    /// <summary>In a format that cannot be sent to the LLM, or damaged.</summary>
    public int ImagesUnreadable { get; set; }

    /// <summary>AI results (picture batches, sections) kept from a failed attempt. A retry reuses them.</summary>
    public int ResultsSaved { get; set; }
    /// <summary>AI results this attempt took from an earlier attempt instead of calling the AI again.</summary>
    public int ResultsReused { get; set; }

    /// <summary>The step that failed and why. Null when ingestion finished.</summary>
    public string? Failure { get; set; }
}

/// <summary>The report as shown on the file card: plain sentences built in C#.</summary>
public class DocumentReport
{
    /// <summary>What happened.</summary>
    public List<string> Done { get; set; } = [];
    /// <summary>What did not happen, so that "done" is not taken to mean "everything".</summary>
    public List<string> NotDone { get; set; } = [];
}
