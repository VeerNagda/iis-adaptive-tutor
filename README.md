# Adaptive Tutor

A prototype tutor that teaches a learner from their own course material and adapts to how well they do. Built as coursework for the Intelligent Interactive Systems module.

A learner creates a course, uploads lecture files and ingests them. The tutor turns the material into topics, teaches each topic in short lessons, quizzes after every lesson, and changes the difficulty and teaching style based on the results. Tests inside a topic and across topics decide what has to be taught again.

## The main design rule: the LLM writes, C# decides

The LLM (Google Gemini) only writes content: topics, lessons, quiz questions, image descriptions and chat answers, always as JSON.

Every decision is plain `if/else` in C#: scoring, pass or fail, difficulty changes, when a test is due, and which topics are weak. This means each decision can be explained and gives the same result every time.

## What has been accomplished

### Course material
- **Courses and uploads.** Create a course and upload PDF, PPTX or DOCX files.
- **Manual ingestion.** An "Ingest" button on each file starts ingestion, so LLM quota is only spent on request. The file's text is extracted, split into chunks of about 400 words, grouped into learning sections by the LLM, and embedded.
- **Image ingestion.** Images inside the uploaded files are extracted and described by the LLM. Each description is added to its page's text as `[Image on this page: ...]` and is then taught like any other text. Plain rules choose which images are sent: none under 5 KB or over 2 MB, a repeated image only once, at most 20 per document, 5 per LLM call.
- **Topics.** Opening the topics list turns the sections of newly ingested documents into topics (title and summary). Existing topics and progress are never changed.

### Teaching
- **Lesson loop.** One session teaches one topic. A topic has one lesson per 1,500 characters of source text (about 240 words), between 1 and 6 lessons. Each lesson has the explanation, an example and a 3-question multiple-choice quiz, all written from that lesson's source text only.
- **Ask the tutor.** Under every lesson the learner can type a question before taking the quiz. The answer is grounded in the lesson and the uploaded material, and follow-up questions work. Asking never changes the difficulty level.
- **Mistake explanations.** Every question stores one sentence per option saying why it is right or wrong, so the learner is told what went wrong with the answer they picked, with no extra LLM call.
- **User control.** "Explain differently" gives a new explanation of the same lesson. "Skip / I know this" moves on without the quiz.

### Adaptation
- **Quiz decision.**

  | Mistakes | Result |
  |---|---|
  | 0 | Pass, extra insight shown, difficulty level +1 (max 5) |
  | 1 | Pass, level unchanged, the mistake is explained |
  | 2 or more | Level -1 (min 1), the same lesson is taught again more simply with new questions |

- **Difficulty and teaching style.** Each topic has a level from 1 (easiest) to 5 (hardest), starting at 3 and kept between sessions. Each level maps to fixed style instructions for the LLM, from plain language with analogies at level 1 to concise, technical content with edge cases at level 5. Every level change is recorded with its reason.
- **Checkpoint test (inside a topic).** After every 3-4 lessons and at the end of a topic: 4 new questions plus up to 2 refresher questions from earlier topics. A topic scoring below 60% is marked weak and drops a level, followed by a review lesson and a re-test (at most 2 rounds).
- **Mandatory topic test (across topics).** After 2 or 3 completed topics a test with 3 new questions per topic must be taken before another topic can be started. A topic below 60% is marked "Needs re-teaching" and drops a level.

### Transparency and visibility
- **Progress bar** for the lessons of the current topic.
- **"Why am I seeing this?"** label on re-taught content, checkpoints and reviews.
- **History page** with past sessions, per-topic status, current level, last test score and every level change with its reason.
- **Mock mode** runs the whole flow with canned content marked "(MOCK)" and no API key. The UI shows a "MOCK MODE" badge.
- **Demo mode** button submits wrong answers on purpose, so the adaptive path can be shown reliably. Its results are labelled as simulated.

## Status

| Status | Features |
|---|---|
| Implemented | Manual ingestion (text and image descriptions), topic extraction and refresh, lesson and quiz loop, ask-the-tutor chat, mistake explanations, adaptive difficulty, checkpoint and review, mandatory topic test, history |
| Simulated | Demo mode; the mock provider when it is used in a demo |
| Planned | Showing images in lessons, learner profile across courses, accessibility review, user study |

Known limits:
- Lessons describe images in words; the pictures themselves are not stored or shown.
- Standalone image files cannot be uploaded, only PDF, PPTX and DOCX.
- The ask-the-tutor chat is kept in the browser and clears on a page refresh.
- There are no automated tests and no login, by design for this prototype.

## How it is built

| Part | Technology | Location |
|---|---|---|
| Frontend | Next.js 16, React 19 | `adaptive_tutor/` |
| API | .NET 10 Web API | `server/server/` |
| Database | SQLite, raw SQL | `server/server/Database/schema.sql` |
| LLM | Google Gemini over plain HTTP, or the mock provider | `server/server/LLM/` |

Files worth reading first:
- `server/server/Services/LearningService.cs` holds all teaching logic and decisions.
- `server/server/LLM/Prompts.cs` holds every prompt, with the reasons for each one.
- `server/server/Services/IngestionService.cs` holds the ingestion pipeline.

Pages:
- `/courses/[id]/learn` shows materials and topics.
- `/courses/[id]/learn/[sessionId]` shows the lesson, chat, quiz and feedback, and also runs the topic test.
- `/courses/[id]/history` shows sessions and per-topic progress.

## Running it

Backend, on http://localhost:5242:

```
cd server/server
dotnet run
```

Frontend, on http://localhost:3000:

```
cd adaptive_tutor
npm install
npm run dev
```

**Gemini key.** Set the `GEMINI_API_KEY` environment variable, or `GeminiAPI:APIKEY` in user-secrets or the git-ignored `appsettings.Development.json`. Never commit a key.

**Mock mode.** With no key, or with `LlmProvider=Mock`, the app uses the mock provider and spends no quota.

**Fresh start.** `adaptive_tutor.db` and `uploads/` are local data and are not committed. Deleting the database file gives a fresh install; the tables are created again on the next start.

## Development record

The commit history is the record of how the prototype was built:

1. Baseline: course, upload and ingestion prototype
2. Stage 1: data model and persistence for adaptive teaching
3. Stage 2: "Start Learning" and a cached topics list
4. Stage 3: lesson and quiz loop
5. Stage 4: adaptive difficulty and teaching style per topic
6. Stage 5: checkpoint test and weak-topic review
7. Stage 6: history view
8. Mistake explanations for wrong answers
9. Topic refresh for newly ingested documents
10. Mandatory topic test after 2-3 completed topics
11. Ask-the-tutor chat on lessons
12. Image ingestion
