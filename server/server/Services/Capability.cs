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

    // ── Where a chat answer comes from (the badge beside every answer) ────────

    public const string FromNotes = "From your notes";
    public const string GeneralKnowledge = "Not in your notes: general knowledge, check it yourself";
    public const string NotFound = "I couldn't find this in your material.";
    public const string AskGeneralAnyway = "Answer from general knowledge anyway";

    public const string OutsideTutor = "Outside what this tutor can do";

    public static string SourceLabel(string source) => source switch
    {
        "notes" => FromNotes,
        "general" => GeneralKnowledge,
        "out_of_scope" => OutsideTutor,
        _ => NotFound,
    };

    // ── Out-of-scope requests ─────────────────────────────────────────────────
    // The model only classifies the request (see Prompts.Ask). The reply is one
    // of these fixed texts: what the limit is, and what the learner can do instead.

    public static string OutOfScope(string? kind) => kind switch
    {
        "exam_prediction" =>
            "I can't predict what will be on your exam. I only know the notes you uploaded, not what your lecturer will ask. "
            + "What I can do: quiz you on these notes, so you can see which parts you know.",
        "grades" =>
            "I can't tell you what grade you will get. My quizzes are short and only cover these notes, so they don't measure your mark. "
            + "What I can do: show your quiz and test results on the History page.",
        "other_course" =>
            "I only know the notes uploaded to this course. "
            + "For another subject, create a course for it and upload its notes.",
        "show_image" =>
            "I can't show pictures. I can describe the pictures in your notes in words, so ask me what a picture shows, "
            + "or open the original file to see it.",
        _ =>
            "That is outside what I can help with here. "
            + "I can explain this lesson, answer questions about your notes, and quiz you.",
    };

    // ── Event log (evidence for the report) ───────────────────────────────────

    public const string UploadRejected = "upload_rejected";
    public const string ScopeWarning = "scope_warning";
    /// <summary>The model said "from the notes" but its quote was not in them, so the label was lowered.</summary>
    public const string SourceDowngrade = "source_downgrade";
    /// <summary>The notes had no answer and the learner asked for a general-knowledge one.</summary>
    public const string GeneralAnswer = "general_answer";
    /// <summary>The learner asked for something the tutor cannot do and got the fixed reply.</summary>
    public const string OutOfScopeRequest = "out_of_scope";
    /// <summary>The learner said a wrong answer was a slip and answered that question again.</summary>
    public const string Slip = "slip";
    /// <summary>The learner reported a quiz question as wrong.</summary>
    public const string QuestionFlagged = "question_flagged";
    /// <summary>A proposed level change was accepted, or overridden with "Keep my level".</summary>
    public const string LevelChoice = "level_change";
    /// <summary>The learner reset a topic's level themselves.</summary>
    public const string LevelReset = "level_reset";
    /// <summary>The learner paused or resumed adaptation.</summary>
    public const string AdaptationPause = "adaptation_pause";

    // ── Messages for the learner's own controls ───────────────────────────────

    public const string SlipMessage =
        "That was a slip: answer this question again, with no penalty. You can do this once per quiz.";
    public const string FlagMessage =
        "Thanks for reporting this question. It no longer counts towards your score and won't be used again.";
    public const string PausedMessage =
        "Adaptation is paused. Your difficulty levels stay where they are; lessons, quizzes and tests carry on as usual.";
    public const string ResumedMessage =
        "Adaptation is on. The tutor will suggest a level change after a quiz, and you can keep your level each time.";
    public static string ResetMessage(string topicTitle, int level) =>
        $"The level of \"{topicTitle}\" was set back to {level}.";

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
