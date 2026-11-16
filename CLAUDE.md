
---

## Product brief: what we are building (prototype)

University coursework prototype for an Intelligent Interactive Systems module. Keep everything simple and readable: clear interaction and explainable design matter more than polish or feature count. No automated tests, no auth, no extra infrastructure.

### What it does
A learner creates a course, uploads materials and ingests them. The tutor turns the material into topics. The learner picks a topic ("Start Learning"); the tutor teaches it in short lessons, quizzes after each one, and adapts the difficulty and teaching style to the results. Tests at two levels (inside a topic, and across topics) decide what has to be taught again.

### Layout
- `server/server` — .NET API, SQLite through `IDbConnectionFactory` and raw `SqliteCommand`.
  - `Services/LearningService.cs` — all teaching logic and decisions.
  - `LLM/Prompts.cs` — every prompt, in one file.
  - `Database/schema.sql` — the complete create DDL.
- `adaptive_tutor` — Next.js frontend.
  - `/courses/[id]/learn` — topics and materials.
  - `/courses/[id]/learn/[sessionId]` — lesson, quiz, feedback (also runs the topic test).
  - `/courses/[id]/history` — past sessions, per-topic progress and difficulty.

### Core rule: the LLM writes, C# decides
The LLM only writes content (topics, lessons, questions) as JSON. Scoring, pass or fail, difficulty changes, when a test is due and which topics are weak are plain if/else in `LearningService`, so every decision can be explained and is repeatable.

### Topics
- Ingestion is started by hand with the "Ingest" button on a file (no background polling by default, so LLM quota is only used on request; the optional `AutoIngest=true` setting starts a 30-second poller and is off by default). It produces `chunks` and `document_sections`.
- Ingestion resumes after a failure: each AI result (a batch of 5 picture descriptions, the sections) is saved in `documents.ingest_progress` as soon as it arrives, under a hash of the exact input that produced it. "Retry" reuses them and only calls the AI for what is missing; the file card says how many results were reused. Cleared when ingestion finishes. Embeddings are not saved (last step, one call per 100 chunks).
- Images inside the PDF, PPTX or DOCX are ingested too: the LLM looks at each image and writes a description (`Prompts.Images`), which is added to that page's text as `[Image on this page: ...]` and from then on is taught like any other text. The image itself is not stored or shown.
  - Which images are sent is plain C# in `IngestionService`: skip files under 5 KB (icons) or over 2 MB, send a repeated image once, at most 20 per document, 5 per LLM call. The LLM returns an empty string for decoration.
  - Standalone image files cannot be uploaded (only PDF, PPTX, DOCX).
- Loading the topics list extracts topics (title + summary) from the sections of documents not yet processed (`documents.topics_extracted = 0`) and appends them. Existing topics and progress are never touched.

### Lesson loop (one session = one topic)
A topic has one lesson step per 1500 characters (about 240 words) of its source text, at least 1 and at most 6. The topic's text is cut into sentences and spread evenly over the steps. Per step:
1. **Lesson:** one LLM call returns the lesson text, an example, an insight and 3 multiple-choice questions, grounded only in that step's source text.
2. **Quiz:** 3 questions. Each has options, the correct index, an explanation and `option_feedback` (one sentence per option saying why it is right or wrong).
3. **Decide:**
   - 0 mistakes: praise plus the insight, next step, the tutor proposes level +1 (max 5).
   - 1 mistake: pass, next step, level unchanged, and the feedback explains what went wrong with that answer (from `option_feedback`).
   - 2 or more mistakes: the tutor proposes level -1 (min 1), the same step is taught again in a different way with new questions. Repeats until passed; "Skip" is always available.

A quiz is handled in two steps. Submitting stores the answers and returns a preview (`Evaluate` with `apply: false`, rolled back). On the feedback screen the learner can use "That was a slip" (one per quiz), "Report a wrong question" (not scored, never reused) and Accept / Keep my level on each proposal. "Next" calls `FinishQuiz`, which runs the same `Evaluate` and commits. A refresh applies the preview with proposals accepted.

### Ask the tutor (chat on a lesson)
- Under every lesson and review lesson the learner can type a question before taking the quiz.
- One LLM call per question (`Prompts.Ask`), given the lesson text and the chunks behind that step as passages with file and page headers.
- The reply JSON carries `intent`, `out_of_scope_kind`, `source`, `source_quote`, `file_name`, `page` and `answer`. C# decides the label (`LearningService.LabelAnswer`): "notes" only if the quote is found in a passage (`QuoteFound`), otherwise "general" or "none"; `out_of_scope` gets a fixed text from `Capability.OutOfScope`.
- A general-knowledge answer is only given after the learner presses "Answer from general knowledge anyway".
- The chat is saved per lesson in `lesson_chat`; the last 5 answered turns are sent back with each question.
- Asking changes nothing: no phase change and no level change. Not available during a quiz.

### Difficulty and teaching style
- Level 1 (easiest) to 5 (hardest) per topic, starts at 3, stored on the topic and reused in later sessions.
- `Prompts.StyleForLevel` maps each level to explicit style instructions (L1 plain language, analogy, small steps; L3 standard; L5 concise, technical, edge cases).
- Every proposal is written to `difficulty_history` with its reason, the number of questions it was based on and the learner's choice (accepted, kept, reset).
- "Pause adaptation" (`courses.adaptation_paused`) stops all level changes; "Reset level" returns a topic to 3.

### Expectation gap (what the tutor can and cannot do)
- Every limit message and threshold is a C# constant or template: `Services/Capability.cs` (card, upload reasons, source labels, out-of-scope replies, event log), `IngestionService` (scope and image limits), `Models/Learning.cs` `Evidence` (question counts, coverage), `LLM/ILlmProvider.cs` `LlmFailure` (failure messages).
- Scope check before ingestion (`IngestionService.CheckScope`): warn above 50 pages or 25,000 words, or when over 30% of pages have no text. The server refuses to ingest a flagged file without a choice (range, first N, continue).
- Ingestion report (`documents.ingest_report`, `IngestionService.Describe`): counts made by the pipeline, shown as done / not done sentences on the file card.
- Every quiz question has a `source_quote`; a question whose quote is not in the source is rejected and the request repeated once (`AskForQuestionsAsync`).
- "Based on N questions" and material coverage (thin under 3,000 characters, rich from 7,500) are shown wherever a score or status is.
- A failed LLM call is shown as a failure with Retry. Never fall back to mock content at run time.
- Each warning, downgrade, slip, report, level choice and out-of-scope request is logged with `Capability.Log` to `capability_events` and listed on the History page.

### Checkpoint test (inside a topic)
- After every 3-4 completed lessons (random) and at the end of the topic.
- 4 new questions on those lessons plus up to 2 stored questions from earlier topics, labelled "Refresher from: X".
- A topic below 60% is marked weak and a level drop is proposed. For the current topic this starts a review lesson followed by a re-test (at most 2 rounds, then move on).

### Mandatory topic test (across topics)
- A completed topic is queued (`topics.awaiting_test`). When 2 or 3 topics are queued (random, stored in `courses.topic_test_every`) the test is due and no other topic can be started until it is taken.
- 3 new questions per queued topic. A topic below 60% is marked weak ("Needs re-teaching") and a level drop is proposed; re-learning it queues it for the next topic test.

### LLM access
- One `ILlmProvider` interface with two implementations: `GeminiProvider` (plain `HttpClient`, JSON output) and `MockLlmProvider` (canned content marked "(MOCK)", so the whole flow runs with no key).
- The key is read from the `GEMINI_API_KEY` environment variable, or `GeminiAPI:APIKEY` from user-secrets / the git-ignored `appsettings.Development.json`. No key, or `LlmProvider=Mock`, means mock mode, and the UI shows a "MOCK MODE" badge.
- Replies are deserialized into the `Llm*` classes in `Models/Learning.cs`. If parsing fails, return a clear error and let the user retry.
- Never commit API keys.

### Database
- `Database/schema.sql` is the single definition of all tables and runs on every start (`IF NOT EXISTS`). A new table goes there.
- A new column goes in `schema.sql` and also in `DatabaseInitializer.AddMissingColumns`, so existing databases get it.
- Tables: `courses`, `documents`, `chunks`, `document_sections`, `topics`, `sessions`, `learn_lessons`, `learn_quizzes`, `learn_questions`, `difficulty_history`, `checkpoint_results`, `capability_events`, `lesson_chat`.
- `adaptive_tutor.db` and `uploads/` are local data and are not committed. Deleting the db file gives a fresh install.

### Frontend principles
- Progress bar (visibility of system status).
- "Explain differently" and "Skip / I know this" buttons, the "Ask the tutor" chat, slips, question reports, Accept / Keep my level, Reset level and Pause adaptation (user control).
- Source badge on every chat answer, "Based on N questions" on every score, and the capability card (honesty about limits).
- Per-answer feedback with the explanation, including what went wrong.
- A "Why am I seeing this?" label on re-taught content, checkpoints and reviews (transparency).
- A "Demo mode" button that submits wrong answers so the adaptive path can be shown reliably; results are labelled as simulated.

### Status labels (used in the report)
- Implemented: manual ingestion (text and image descriptions), topic extraction and refresh, lesson and quiz loop, ask-the-tutor chat (saved), mistake explanations, adaptive difficulty with level proposals, checkpoint and review, mandatory topic test, history, scope check, ingestion report, source labels and quote checks, evidence labels, out-of-scope replies, slips, question reports, stored-data panel, failure messages, capability event log.
- Simulated: demo mode; mock provider if used in the demo, including its trigger words ("outside", "wrongquote", "exam", "grade") and the deliberately flawed third lesson question.
- Planned: multimodal teaching (showing images in lessons), standalone image uploads, learner profile across courses, accessibility review, user study.

### Working rules
- Small commits with clear messages (the commit history is assessed as the development record).
- Do not create extra files beyond what is needed.
- Comment the LLM code clearly; I am new to LLM APIs and must explain every choice.
- When testing, use mock mode (`LlmProvider=Mock`) so no Gemini quota is spent.
