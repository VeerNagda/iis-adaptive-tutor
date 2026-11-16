# Adaptive Tutor

A prototype tutor that teaches a learner from their own course material and adapts to how well they do. Built as coursework for the Intelligent Interactive Systems module.

Prose word count: about 1,600 words (tables, code blocks and headings not counted).

A learner creates a course, uploads lecture files and ingests them. The tutor turns the material into topics, teaches each topic in short lessons, quizzes after every lesson, and suggests changes to the difficulty based on the results. Tests inside a topic and across topics decide what has to be taught again.

The second half of the work is about honesty: closing the gap between what a learner assumes the tutor can do and what it can reliably do.

## The main design rule: the LLM writes, C# decides

The LLM (Google Gemini) only writes content: topics, lessons, quiz questions, image descriptions and chat answers, always as JSON.

Every decision is plain `if/else` in C#: scoring, pass or fail, level proposals, when a test is due, which topics are weak, where an answer is labelled as coming from, and what counts as out of scope. Each decision can be explained and gives the same result every time. Every threshold and every message about a limit is a named C# constant or template, not text inside a prompt.

## What has been accomplished

### Course material
- **Courses and uploads.** Create a course and upload PDF, PPTX or DOCX files.
- **Manual ingestion.** An "Ingest" button on each file starts ingestion, so LLM quota is only spent on request. The text is extracted, split into chunks of about 400 words, grouped into sections by the LLM, and embedded.
- **Image ingestion.** Images inside the files are described in words by the LLM. Each description is added to its page's text as `[Image on this page: ...]`.
- **Topics.** Opening the topics list turns the sections of newly ingested documents into topics. Existing topics and progress are never changed.

### Teaching
- **Lesson loop.** One session teaches one topic. A topic has one lesson per 1,500 characters of source text, between 1 and 6. Each lesson has an explanation, an example and a 3-question quiz written from that lesson's source text only.
- **Ask the tutor.** Under every lesson the learner can ask questions before the quiz. The chat is saved with the lesson.
- **Mistake explanations.** Every question stores one sentence per option saying why it is right or wrong.
- **User control.** "Explain differently" and "Skip / I know this".

### Adaptation
- **Quiz decision.**

  | Mistakes | Result |
  |---|---|
  | 0 | Pass, extra insight, the tutor proposes level +1 (max 5) |
  | 1 | Pass, level unchanged, the mistake is explained |
  | 2 or more | The tutor proposes level -1 (min 1); the same lesson is taught again in a different way with new questions |

- **Difficulty and teaching style.** Each topic has a level from 1 to 5, starting at 3. Each level maps to fixed style instructions for the LLM.
- **Checkpoint test (inside a topic).** After every 3-4 lessons and at the end of a topic. A topic below 60% is marked weak, followed by a review lesson and a re-test.
- **Mandatory topic test (across topics).** After 2 or 3 completed topics, before another topic can be started.

### Closing the expectation gap
These features were added in six stages. Each is visible in the UI, decided by C# rules, and works in mock mode.

- **Scope check (stage 1).** Before ingestion the server counts pages, words and estimated chunks. It warns about textbook-sized files (over 50 pages or 25,000 words) and scans (over 30% of pages without text). A popup gives the reason and four choices: a page range, the first N pages, continue anyway, or cancel. Pictures, video, audio and ZIP files are refused with a reason and what to do instead. A permanent "What this tutor can and can't do" card sits on the course page.
- **Honest ingestion report (stage 2).** The file card says what ingestion really did, from its own counts: pages read and skipped, pictures described and skipped under each rule, chunks, sections and topics. What did not happen is listed separately, and a failure names the step where it stopped, with Retry.
- **Source labels (stage 3).** Every chat answer carries a badge. "From your notes" is only shown when C# finds the model's quote in the uploaded material; the quote, file, page and upload date are shown beside the answer. A claim that fails the check is lowered to "Not in your notes: general knowledge, check it yourself". When the notes have no answer the tutor says so and offers "Answer from general knowledge anyway". Quiz questions carry a checked quote too, shown after answering.
- **Evidence strength (stage 4).** "Based on N questions" appears next to scores, level proposals, level history and topic status. Each topic has a "Material coverage" label (thin, adequate or rich). Requests the tutor cannot serve (exam prediction, grades, another course, showing an image) get a fixed reply written in C#.
- **Agency and error recovery (stage 5).** A level change is a proposal with its cause and "Accept" / "Keep my level". "That was a slip" lets one wrong answer per quiz be answered again. "Report a wrong question" removes a question from the score. The History page shows what the tutor has stored, with "Reset level" and "Pause adaptation".
- **Failure honesty (stage 6).** A failed AI call gives one plain message and Retry. The app never falls back to mock content by itself.
- **Evidence log.** Every warning, label change, slip, report, level choice and out-of-scope request is written to `capability_events` and listed on the History page.

### Transparency and visibility
- **Progress bar** for the lessons of the current topic.
- **"Why am I seeing this?"** label on re-taught content, checkpoints and reviews.
- **History page** with stored data and controls, per-topic progress, level history with the learner's choices, capability events and past sessions.
- **Mock mode** runs the whole flow with canned content marked "(MOCK)" and no API key. Every learning page shows a "MOCK MODE" badge.
- **Demo mode** button submits wrong answers on purpose. Its results are labelled as simulated.

## Status

| Status | Features |
|---|---|
| Implemented | Manual ingestion (text and image descriptions), topic extraction and refresh, lesson and quiz loop, ask-the-tutor chat saved per lesson, mistake explanations, checkpoint and review, mandatory topic test, history. Scope check and upload reasons, capability card, ingestion report, source labels with the quote check, quiz question quotes, evidence labels, material coverage, out-of-scope replies, level proposals, slips, question reports, stored-data panel with reset and pause, plain failure messages, capability event log |
| Simulated | Demo mode. The mock provider when used in a demo, including its trigger words: a chat question containing "outside" plays "not in the notes", "wrongquote" plays a false "from the notes" claim, and "exam", "grade", "another course" or "show ... image" play out-of-scope requests. The third question of each mock lesson quiz is deliberately flawed so reporting can be shown |
| Planned | Showing images in lessons, standalone image uploads, learner profile across courses, accessibility review, user study |

Known limits:
- The stage 3 to 6 behaviour was checked with the mock provider. How often real Gemini quotes its source exactly, classifies a request correctly, or has a question rejected has not been measured.
- The quota, timeout and API-key failure messages were not triggered for real; only "could not be reached" was, with the network blocked.
- Lessons describe images in words; the pictures themselves are not stored or shown. A quote taken from one of these descriptions is marked as the tutor's own description.
- A slip is taken on trust: the feedback already shows the correct answer when the learner answers again. It is limited to one per quiz and counted on the History page.
- A topic can be marked weak from one or two refresher questions. The "Based on N questions" label makes this visible but the rule itself is unchanged.
- A file ingested with a page range cannot be extended later; it has to be removed and ingested again.
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
- `server/server/Services/IngestionService.cs` holds the ingestion pipeline, the scope check and the ingestion report.
- `server/server/Services/Capability.cs` holds every message about what the tutor can and cannot do, and the event log.
- `server/server/LLM/Prompts.cs` holds every prompt, with the reasons for each one.

Pages:
- `/courses/[id]/learn` shows materials, the capability card and topics.
- `/courses/[id]/learn/[sessionId]` shows the lesson, chat, quiz and feedback, and also runs the topic test.
- `/courses/[id]/history` shows stored data, progress, capability events and sessions.

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

**Docker.** From the repository root, `docker compose up --build` starts both parts: the frontend on http://localhost:3000 and the backend on http://localhost:5242. With no key it runs in mock mode. To use Gemini, copy `.env.example` to `.env` and set `GEMINI_API_KEY`. The database and uploads are kept in the `tutor-data` volume; `docker compose down -v` gives a fresh install.

**Gemini key.** Set the `GEMINI_API_KEY` environment variable, or `GeminiAPI:APIKEY` in user-secrets or the git-ignored `appsettings.Development.json`. Never commit a key.

**Mock mode.** With no key, or with `LlmProvider=Mock`, the app uses the mock provider and spends no quota. Mock mode is chosen at start-up only.

**Fresh start.** `adaptive_tutor.db` and `uploads/` are local data and are not committed. Deleting the database file gives a fresh install. New tables and columns are added to an existing database on the next start.

## Development record

The commit history is the record of how the prototype was built.

First version:

1. Baseline: course, upload and ingestion prototype
2. Stages 1 to 6 of the first version: data model, topics list, lesson and quiz loop, adaptive difficulty, checkpoint and review, history
3. Mistake explanations, topic refresh, mandatory topic test
4. Ask-the-tutor chat, image ingestion
5. Fixes: stray characters around LLM JSON, a longer Gemini timeout, lesson count based on text length

Closing the expectation gap:

| Stage | What changed | Why | What was checked |
|---|---|---|---|
| 1. Scope check | Page, word and chunk counts before ingestion; warnings with four choices; reasons for refused file types; capability card; `capability_events` table | Fluent output makes learners expect more than the tutor can do, and a warning afterwards does not fix that (G1, G2, G10) | Mock mode: a 65-page file was warned and refused without a choice; a page range ingested only those pages; four file types refused with their reasons; events logged |
| 2. Ingestion report | Counts kept during ingestion and shown as "done" and "not done" sentences; failure names its step | "Done" is a claim until the return values say so | Mock mode: slide deck, image-heavy PDF, page range and a broken PDF each gave a report matching the database |
| 3. Source labels | Chat JSON with source, quote, file and page; quote checked in C#; downgrade; opt-in general answers; checked quotes on quiz questions | Grounding does not guarantee truth, and "answer only from the passages" is an instruction, not a mechanism | Mock mode: notes answer with quote and file; false claim downgraded and logged; no answer until the learner asked for a general one; quiz feedback carried quotes |
| 4. Evidence strength | "Based on N questions" everywhere a score drives something; coverage labels; out-of-scope intent with fixed C# replies | Three questions are weak evidence, and a model asked to predict an exam answers plausibly (G11) | Mock mode: coverage and evidence on topics; four out-of-scope kinds returned their fixed text; "example" did not trigger "exam" |
| 5. Agency and recovery | Two-step quiz (preview, then apply); level proposals; slip; question report; stored-data panel with reset and pause; saved chat | Slips need undo, mistakes need better feedback, and agency needs visible cause and control (G17, G18) | Mock mode: preview changed nothing; slip, report, keep, accept-by-default, pause and reset behaved as designed; checkpoint and re-test still ran |
| 6. Failure honesty | Plain failure messages chosen in C#; Retry on the chat; mock badge on History | A failure must look like a failure, never like generated content | Real-provider mode with the network blocked: lesson and ingest failed with the message, stored nothing, no mock content |
| Evidence | Capability events section on the History page | The report needs countable evidence of these moments | Mock mode: counts and recent items matched the table |
| Browser check | Three small fixes (file card refresh, chat turn order, log wording) | Found by using the screens rather than the API | Every new screen clicked through once against a mock backend and a copy of the database; no console errors |

## AI assistance

Claude (Anthropic's AI assistant, used through Claude Code) helped write the code in this repository, including the prompts, the C# decision logic, the frontend screens and this README. The design rule, the feature list and the stage plan came from the author.

Checked by Claude while building:
- The backend builds without errors or warnings, and the frontend passes the TypeScript check and the linter.
- Each stage was run through its API in mock mode against a copy of the database, as listed in the table above.
- The new screens were clicked through once in a browser, in mock mode.

Not checked by Claude:
- Anything with the real Gemini model after stage 2. No quota was spent on stages 3 to 6.
- Accessibility, mobile layout, and behaviour with more than one learner.

Checked by hand by the author (to be completed; nothing below has been confirmed yet):
- [ ] Each stage's screen reads correctly and the wording is right for learners.
- [ ] One full topic with real Gemini: quotes are found, labels look right, no question is wrongly rejected.
- [ ] An out-of-scope question and a not-in-the-notes question with real Gemini.
- [ ] The existing database upgraded cleanly and earlier sessions still open.
- [ ] The commit history matches the development record above.
