---

## Product brief: what we are building (prototype)

University coursework prototype for an Intelligent Interactive Systems module. Keep everything simple and readable: clear interaction and explainable design matter more than polish or feature count. No automated tests, no auth, no extra infrastructure.

### What it does
A learner picks a course with uploaded materials and starts a session. The tutor splits the material into sections, teaches each in a short chunk, quizzes the learner, and adapts based on the score.

### Core loop (per section)
1. **Teach:** short explanation (~150 words) plus one example, in `standard` or `simplified` mode.
2. **Quiz:** 4 multiple-choice questions generated from that section's text only. Each has options, a correct answer, an explanation and a concept tag.
3. **Score** in plain C# (correct / total) and record the missed concepts.
4. **Decide** with simple if/else logic in C#, NOT the LLM:
   - Score >= 75%: praise, one short insight, advance to the next section.
   - Score < 75%: re-teach the same section simply (analogy, shorter sentences, focus on missed concepts), then re-quiz with NEW questions. After 2 failed attempts, flag the section for review and let the learner move on.
5. **Recall:** from section 3 onward, sometimes inject 2 questions from earlier sections (weighted toward weaker concepts), labelled "Refresher from Section X".

### LLM access
- One `ILlmProvider` interface with two implementations, chosen by config: `MockLlmProvider` (canned sections and quizzes so the app runs with no key) and `GeminiProvider` (simple `HttpClient` call).
- LLM replies are JSON; deserialize into simple classes. If parsing fails, return a clear error and let the user retry.
- Never commit API keys. Read the key from an environment variable or `dotnet user-secrets`. Add the key name (empty value) to an example config only.

### Backend additions (extend the existing structure)
- Reuse `IDbConnectionFactory`, raw `SqliteCommand`, and add new tables via `DatabaseInitializer.RunMigrations()` following the existing pattern.
- New tables (keep minimal): `sections` (course/document, order, title, text), `questions` (section, text, options, correct answer, explanation, concept), `attempts` (session, section, attempt number, score, missed concepts, mode).
- Services: sectioning (extract text from uploaded files and split into 4-8 sections via LLM), teach, quiz, scoring and decision logic, recall.
- Endpoints under `/courses/{id}/sessions/{sessionId}/`: get next teaching content, get quiz, submit answers (returns score, decision, next action), skip section, progress.
- File text extraction: PdfPig (PDF), DocumentFormat.OpenXml (DOCX), plain read for TXT.

### Frontend (use the existing route `/courses/[id]/chat/[sessionId]`)
Keep it to a few states on one page: teach, quiz, feedback.
- Progress bar (visibility of system status).
- "Explain differently" and "Skip / I know this" buttons (user control).
- Per-answer feedback with the explanation.
- A small label explaining why content appears, e.g. "Re-explained because you missed X" or "Refresher from Section 2" (transparency).
- A "Demo mode" button that submits scripted wrong answers so the re-teach path can be shown reliably (label as simulated).

### Status labels (used in the report)
- Implemented: sectioning, teach, quiz, scoring, adaptive branch, recall.
- Simulated: demo mode; mock provider if used in the demo.
- Planned: multimodal teaching, persistent learner profile across sessions, accessibility review, user study.

### Working rules
- Build one step at a time and wait for review before the next.
- Small commits with clear messages (the commit history is assessed as the development record).
- Do not create extra files beyond what is needed.
- Comment the LLM code clearly; I am new to LLM APIs and must explain every choice.

### Build order
1. `ILlmProvider` plus mock provider
2. Text extraction and sectioning for an uploaded document
3. Teach, quiz and score for one section
4. Adaptive branch (pass / re-teach / re-quiz / move on)
5. Recall questions
6. Chat page UI
7. Gemini provider and README