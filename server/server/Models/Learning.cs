using System.Text.Json.Serialization;

namespace server.Models;

// ══ 1. Shapes the LLM must return (structured JSON output) ═══════════════════
// The prompts in LLM/Prompts.cs describe exactly these shapes, and the JSON
// reply is deserialized straight into these classes. [JsonPropertyName] pins
// the JSON key so the prompt and the class cannot drift apart silently.

/// <summary>Ingestion: one description per image sent, in the same order ("" = decoration).</summary>
public class LlmImageDescriptions
{
    [JsonPropertyName("descriptions")] public List<string> Descriptions { get; set; } = [];
}

public class LlmTopicList
{
    [JsonPropertyName("topics")] public List<LlmTopic> Topics { get; set; } = [];
}

public class LlmTopic
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    /// <summary>Ids of the document sections this topic is built from.</summary>
    [JsonPropertyName("section_ids")] public List<int> SectionIds { get; set; } = [];
}

/// <summary>One lesson and its quiz, returned together in a single LLM call.</summary>
public class LlmLesson
{
    [JsonPropertyName("lesson_text")] public string LessonText { get; set; } = "";
    [JsonPropertyName("example")] public string? Example { get; set; }
    /// <summary>One extra "good to know" point, shown only after a passed quiz.</summary>
    [JsonPropertyName("insight")] public string? Insight { get; set; }
    [JsonPropertyName("questions")] public List<LlmQuestion> Questions { get; set; } = [];
}

public class LlmQuiz
{
    [JsonPropertyName("questions")] public List<LlmQuestion> Questions { get; set; } = [];
}

public class LlmQuestion
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("options")] public List<string> Options { get; set; } = [];
    /// <summary>0-based position of the correct option in <see cref="Options"/>.</summary>
    [JsonPropertyName("correct_index")] public int CorrectIndex { get; set; }
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
    /// <summary>
    /// One sentence per option, in the same order as <see cref="Options"/>: why that
    /// option is right or wrong. Lets the app explain a mistake without another LLM call.
    /// </summary>
    [JsonPropertyName("option_feedback")] public List<string> OptionFeedback { get; set; } = [];
    /// <summary>
    /// The words of the learner's notes the correct answer is based on. A question
    /// whose quote cannot be found in the notes is rejected (LearningService.QuoteFound).
    /// </summary>
    [JsonPropertyName("source_quote")] public string? SourceQuote { get; set; }
}

/// <summary>The tutor's answer to a question the learner asked about a lesson.</summary>
public class LlmAnswer
{
    /// <summary>Where the model says the answer comes from: "notes", "general" or "none".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    /// <summary>For "notes": words copied from a passage. Checked in C# before it is believed.</summary>
    [JsonPropertyName("source_quote")] public string? SourceQuote { get; set; }
    /// <summary>
    /// The model's own idea of the file and page. Only kept for comparison: what is
    /// shown comes from the passage in which C# actually found the quote. (page is
    /// read loosely because a model may write 3, "3" or "3-5".)
    /// </summary>
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("page")] public System.Text.Json.JsonElement? Page { get; set; }
    [JsonPropertyName("answer")] public string? Answer { get; set; }
}

// ══ 2. Database rows used by LearningService ═════════════════════════════════

public class Topic
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public int SequenceNumber { get; set; }
    public int DifficultyLevel { get; set; } = 3;
    public string Status { get; set; } = "new";
    /// <summary>Not sent to the browser; ids of the source chunks.</summary>
    [JsonIgnore] public List<int> ChunkIds { get; set; } = [];
    /// <summary>Not sent to the browser; characters of source text, which sets the number of lessons.</summary>
    [JsonIgnore] public int SourceLength { get; set; }
}

public class LearnSession
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    /// <summary>0 for a mandatory topic test, which covers several topics.</summary>
    public int TopicId { get; set; }
    /// <summary>"topic" = lessons on one topic, "test" = mandatory topic test.</summary>
    public string Kind { get; set; } = "topic";
    public string Status { get; set; } = "active";
    public string Phase { get; set; } = "lesson";
    public int StepIndex { get; set; }
    public int Attempt { get; set; } = 1;
    public int SinceCheckpoint { get; set; }
    public int CheckpointEvery { get; set; } = 3;
    public int CheckpointFrom { get; set; }
    public int ReviewRound { get; set; }
    public string? Reason { get; set; }
}

// ══ 3. API responses sent to the frontend ════════════════════════════════════

public class TopicListResponse
{
    public bool MockMode { get; set; }
    /// <summary>False when no document of the course has been ingested yet.</summary>
    public bool HasContent { get; set; }
    /// <summary>How many topics this call added from newly ingested documents.</summary>
    public int NewTopics { get; set; }
    /// <summary>True when a mandatory topic test must be taken before starting another topic.</summary>
    public bool TestDue { get; set; }
    /// <summary>Titles of the completed topics the next topic test will cover.</summary>
    public List<string> TestTopics { get; set; } = [];
    public List<Topic> Topics { get; set; } = [];
}

/// <summary>What the learner should see right now: a lesson, a quiz, or the end screen.</summary>
public class StepResponse
{
    public string Type { get; set; } = "";          // lesson | quiz | done
    public string Phase { get; set; } = "";
    public bool MockMode { get; set; }
    public string TopicTitle { get; set; } = "";
    public int Level { get; set; }
    public string StyleLabel { get; set; } = "";
    public int StepNumber { get; set; }             // 1-based number of the current lesson
    public int CompletedSteps { get; set; }         // lessons finished so far, for the progress bar
    public int TotalSteps { get; set; }
    public string? Reason { get; set; }             // "why am I seeing this?"
    public LessonDto? Lesson { get; set; }
    public QuizDto? Quiz { get; set; }
    public string? Message { get; set; }
}

public class LessonDto
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string? Example { get; set; }
}

public class QuizDto
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";          // lesson | checkpoint | retest | topic_test
    public List<QuestionDto> Questions { get; set; } = [];
}

/// <summary>A question as shown before answering: no correct answer included.</summary>
public class QuestionDto
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string? Label { get; set; }              // e.g. "Refresher from: Topic X"
}

/// <summary>A question the learner asks about the lesson on screen ("Ask the tutor").</summary>
public class AskRequest
{
    public string Question { get; set; } = "";
    /// <summary>
    /// Earlier questions and answers of this chat, oldest first. The browser keeps
    /// the chat and sends it back, because the LLM has no memory between calls.
    /// </summary>
    public List<ChatTurn> History { get; set; } = [];
    /// <summary>
    /// True only when the learner pressed "Answer from general knowledge anyway"
    /// after being told the notes do not cover the question.
    /// </summary>
    public bool AllowGeneral { get; set; }
}

public class ChatTurn
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

/// <summary>
/// A chat answer with where it comes from. Source is decided in C#, not taken
/// from the model: "notes" only when the quote was found in a passage.
/// </summary>
public class AskResponse
{
    public string Answer { get; set; } = "";
    /// <summary>"notes", "general" or "none".</summary>
    public string Source { get; set; } = "";
    /// <summary>The badge text for this source (a fixed C# constant).</summary>
    public string Label { get; set; } = "";
    // Filled only for "notes": the quote and where it was found.
    public string? SourceQuote { get; set; }
    public string? FileName { get; set; }
    public string? PageLabel { get; set; }
    public string? UploadedAt { get; set; }
    /// <summary>True when the quote is from the AI's description of a picture, not the author's own words.</summary>
    public bool FromPictureDescription { get; set; }
    /// <summary>True when "Answer from general knowledge anyway" should be offered.</summary>
    public bool CanAskGeneral { get; set; }
}

public class SubmitAnswersRequest
{
    /// <summary>Chosen option index for each question, in the order the quiz was shown.</summary>
    public List<int> Answers { get; set; } = [];
    /// <summary>Demo mode: the server fills in wrong answers so the re-teach path can be shown.</summary>
    public bool SimulateWrong { get; set; }
}

public class AnswerResult
{
    public int Correct { get; set; }
    public int Total { get; set; }
    public bool Passed { get; set; }
    public bool Simulated { get; set; }
    public string Decision { get; set; } = "";      // advance | reteach | checkpoint | review | done
    public string Message { get; set; } = "";
    public string? Insight { get; set; }
    public int OldLevel { get; set; }
    public int NewLevel { get; set; }
    public List<QuestionFeedback> Questions { get; set; } = [];
    public List<TopicScore> TopicScores { get; set; } = [];   // checkpoints only
}

public class QuestionFeedback
{
    public int QuestionId { get; set; }
    public int UserAnswer { get; set; }
    public int CorrectIndex { get; set; }
    public bool IsCorrect { get; set; }
    /// <summary>Why the correct option is right.</summary>
    public string? Explanation { get; set; }
    /// <summary>Only for a wrong answer: why the option the learner picked is wrong.</summary>
    public string? WhatWentWrong { get; set; }
    /// <summary>The words of the learner's notes the question is based on ("From your notes: ...").</summary>
    public string? SourceQuote { get; set; }
}

public class TopicScore
{
    public int TopicId { get; set; }
    public string TopicTitle { get; set; } = "";
    public int Correct { get; set; }
    public int Total { get; set; }
    public int ScorePct { get; set; }
    public bool IsWeak { get; set; }
}

public class HistoryResponse
{
    public List<SessionHistory> Sessions { get; set; } = [];
    public List<TopicProgress> Topics { get; set; } = [];
}

public class SessionHistory
{
    public int Id { get; set; }
    public string TopicTitle { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public int Lessons { get; set; }
    public int Quizzes { get; set; }
    public int QuizzesPassed { get; set; }
}

public class TopicProgress
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int Level { get; set; }
    public int? LastCheckpointPct { get; set; }
    public List<LevelChange> LevelChanges { get; set; } = [];
}

public class LevelChange
{
    public int OldLevel { get; set; }
    public int NewLevel { get; set; }
    public string Reason { get; set; } = "";
    public string At { get; set; } = "";
}
