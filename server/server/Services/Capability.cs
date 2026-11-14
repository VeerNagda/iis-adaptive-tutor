using Microsoft.Data.Sqlite;

namespace server.Services;

/// <summary>
/// What the tutor can and cannot do, said in plain words, in one place.
///
/// Learners tend to expect more from a fluent system than it can deliver (the
/// "expectation gulf"). Every message that sets or corrects an expectation is a
/// constant or template here, never text written by the LLM, so the wording is
/// the same every time and can be quoted in the report.
///
/// Each time one of these messages is shown, or the learner makes a choice
/// about one, a row is written to capability_events (see <see cref="Log"/>).
/// </summary>
public static class Capability
{
    // ── The permanent "What this tutor can and can't do" card ─────────────────

    public static readonly string[] Can =
    [
        "Teach from the notes you upload (PDF, PPTX or DOCX).",
        "Describe the pictures in your notes in words.",
        "Quiz you after each lesson and adjust the difficulty.",
        "Answer questions about the lesson you are reading.",
    ];

    public static readonly string[] Cannot =
    [
        "It only knows your uploaded notes. It has not read your textbook or the internet.",
        "It can't show pictures, only describe them.",
        "It can't read standalone images, video, audio or ZIP files.",
        "The chat on a lesson clears when you refresh the page.",
        "It can't predict exam questions or your grades.",
    ];

    // ── Upload: which files are accepted, and why the others are not ──────────

    public static readonly string[] AcceptedExtensions = [".pdf", ".pptx", ".docx"];

    public const string OtherFileReason =
        "The tutor can only read PDF, PPTX and DOCX files.";

    /// <summary>File types learners often try, each with a reason and what to do instead.</summary>
    private static readonly (string[] Extensions, string Reason)[] Unsupported =
    [
        ([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".tif", ".tiff", ".svg"],
            "The tutor can't read a picture on its own. Put it in a PDF, PPTX or DOCX and it will be described in words."),
        ([".mp4", ".mov", ".avi", ".mkv", ".webm"],
            "The tutor can't watch videos. Upload the slides or notes that go with it."),
        ([".mp3", ".wav", ".m4a", ".ogg", ".flac"],
            "The tutor can't listen to recordings. Upload a transcript or your notes instead."),
        ([".zip", ".rar", ".7z", ".tar", ".gz"],
            "The tutor can't open ZIP files. Unzip it and upload the PDF, PPTX or DOCX files inside."),
    ];

    /// <summary>Extension (with the dot) → reason, for the file types listed above.</summary>
    public static Dictionary<string, string> RejectReasons =>
        Unsupported.SelectMany(u => u.Extensions.Select(e => (e, u.Reason))).ToDictionary(p => p.e, p => p.Reason);

    public static string RejectReason(string extension) =>
        RejectReasons.GetValueOrDefault(extension.ToLowerInvariant(), OtherFileReason);

    // ── Event log (evidence for the report) ───────────────────────────────────

    public const string UploadRejected = "upload_rejected";
    public const string ScopeWarning = "scope_warning";

    /// <summary>Event types the browser may log itself (a choice the server never sees, such as "Cancel").</summary>
    public static readonly string[] ClientEvents = [ScopeWarning];

    /// <summary>Records that a capability message was shown and what the learner chose.</summary>
    public static void Log(SqliteConnection conn, int courseId, string eventType, string shownMessage, string? userChoice)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO capability_events (course_id, event_type, shown_message, user_choice)
            VALUES ($c, $type, $message, $choice)
            """;
        cmd.Parameters.AddWithValue("$c", courseId);
        cmd.Parameters.AddWithValue("$type", eventType);
        cmd.Parameters.AddWithValue("$message", shownMessage);
        cmd.Parameters.AddWithValue("$choice", (object?)userChoice ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
