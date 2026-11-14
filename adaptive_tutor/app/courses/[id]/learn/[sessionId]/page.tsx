"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { type FormEvent, Suspense, useCallback, useEffect, useState } from "react";
import { MockBadge } from "../../../../components/MockBadge";
import { apiService } from "../../../../services/apiService";

// ─── Types (match the backend's Models/Learning.cs) ──────────────────────────

type Question = { id: number; text: string; options: string[]; label: string | null };
type Quiz = { id: number; kind: "lesson" | "checkpoint" | "retest" | "topic_test"; questions: Question[] };

type Step = {
  type: "lesson" | "quiz" | "done";
  phase: string;
  mockMode: boolean;
  topicTitle: string;
  level: number; // difficulty 1..5
  styleLabel: string;
  stepNumber: number; // current lesson, 1-based
  completedSteps: number; // lessons finished so far
  totalSteps: number;
  reason: string | null; // why this content is shown
  lesson: { id: number; text: string; example: string | null; chat: ChatTurn[] } | null; // chat = saved questions and answers
  quiz: Quiz | null;
  message: string | null;
};

type AnswerResult = {
  correct: number;
  total: number;
  passed: boolean;
  simulated: boolean;
  decision: "advance" | "reteach" | "checkpoint" | "review" | "done";
  message: string;
  insight: string | null;
  oldLevel: number;
  newLevel: number;
  evidence: string; // "Based on N questions"
  // Level changes the tutor suggests. Nothing changes until the learner has chosen and pressed Next.
  levelProposals: { topicId: number; topicTitle: string; oldLevel: number; newLevel: number; message: string; evidence: string }[];
  levelNote: string | null; // set while adaptation is paused
  slipAvailable: boolean; // "That was a slip" can be used once per quiz
  questions: {
    questionId: number;
    userAnswer: number;
    correctIndex: number;
    isCorrect: boolean;
    explanation: string | null; // why the correct option is right
    whatWentWrong: string | null; // why the option the learner picked is wrong
    sourceQuote: string | null; // the words of the notes the question is based on
    slip: boolean; // answered again after "That was a slip"
    flagged: boolean; // reported as a wrong question: not counted
  }[];
  topicScores: { topicId: number; topicTitle: string; correct: number; total: number; scorePct: number; isWeak: boolean; evidence: string }[];
};

// The page is always in exactly one of these states.
type View =
  | { kind: "loading"; text: string }
  | { kind: "error"; text: string }
  | { kind: "step"; step: Step } // lesson, quiz or done
  | { kind: "feedback"; step: Step; result: AnswerResult };

const QUIZ_TITLE: Record<Quiz["kind"], string> = {
  lesson: "Quick quiz",
  checkpoint: "Checkpoint test",
  retest: "Re-test",
  topic_test: "Mandatory topic test",
};

const NEXT_LABEL: Record<AnswerResult["decision"], string> = {
  advance: "Next lesson",
  reteach: "See the new explanation",
  checkpoint: "Start the checkpoint test",
  review: "Start the review",
  done: "Finish",
};

// ─── Small pieces ────────────────────────────────────────────────────────────

// Visibility of system status: the bar fills as lessons are completed.
function ProgressBar({ current, completed, total }: { current: number; completed: number; total: number }) {
  const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
  return (
    <div className="flex items-center gap-3">
      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-zinc-200">
        <div className="h-full rounded-full bg-zinc-900 transition-all duration-500" style={{ width: `${pct}%` }} />
      </div>
      <span className="shrink-0 text-xs text-zinc-500">
        {completed >= total ? `All ${total} lessons done` : `Lesson ${current} of ${total}`}
      </span>
    </div>
  );
}

// Transparency: a small label explaining why this content is shown.
function ReasonLabel({ text }: { text: string }) {
  return (
    <p className="mb-4 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-xs text-blue-800">
      <span className="font-semibold">Why am I seeing this? </span>
      {text}
    </p>
  );
}

// User control: the learner can clear up a doubt before moving on to the quiz.
// The chat is saved with the lesson on the server, which answers each question from
// the lesson and the uploaded material, and is sent the earlier turns so that
// follow-up questions make sense. Asking never changes the difficulty level.
//
// Every answer carries a label saying where it comes from. The server decides
// the label: "notes" only when it found the quote in the uploaded material.
type ChatAnswer = {
  id: number; // id of the saved turn
  question: string;
  answer: string;
  // out_of_scope: something the tutor cannot do; the answer is then a fixed text from the server.
  source: "notes" | "general" | "none" | "out_of_scope";
  label: string; // badge text, written by the server
  sourceQuote: string | null;
  fileName: string | null;
  pageLabel: string | null;
  uploadedAt: string | null;
  fromPictureDescription: boolean;
  canAskGeneral: boolean;
};
type ChatTurn = ChatAnswer;

const SOURCE_STYLE: Record<ChatAnswer["source"], string> = {
  notes: "bg-green-100 text-green-800",
  general: "bg-amber-100 text-amber-900",
  none: "bg-zinc-200 text-zinc-700",
  out_of_scope: "bg-purple-100 text-purple-800",
};

// SQLite stores UTC timestamps as "YYYY-MM-DD HH:MM:SS"; show the local date.
function formatDate(raw: string) {
  const d = new Date(raw.replace(" ", "T") + "Z");
  return isNaN(d.getTime()) ? raw : d.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

// saved = the turns stored on the server for this lesson, so the chat is still there after a refresh.
function LessonChat({ askUrl, disabled, saved }: { askUrl: string; disabled: boolean; saved: ChatTurn[] }) {
  const [turns, setTurns] = useState<ChatTurn[]>(saved);
  const [draft, setDraft] = useState("");
  const [asking, setAsking] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // allowGeneral is only true when the learner pressed "Answer from general
  // knowledge anyway" on a question the notes did not cover; that answer then
  // replaces the "couldn't find this" turn.
  async function send(question: string, allowGeneral: boolean, replaceIndex?: number) {
    if (!question || asking) return;
    setAsking(true);
    setError(null);
    try {
      // The server keeps the chat, so only the new question is sent.
      const replaceTurnId = replaceIndex === undefined ? null : turns[replaceIndex].id;
      const turn = await apiService.post<ChatAnswer>(askUrl, { question, allowGeneral, replaceTurnId });
      setTurns((prev) => (replaceIndex === undefined ? [...prev, turn] : prev.map((t, i) => (i === replaceIndex ? turn : t))));
      if (replaceIndex === undefined) setDraft("");
    } catch (err) {
      // The question stays in the box so it can be sent again.
      setError(err instanceof Error ? err.message : "Something went wrong.");
    } finally {
      setAsking(false);
    }
  }

  function ask(e: FormEvent) {
    e.preventDefault();
    send(draft.trim(), false);
  }

  return (
    <div className="mt-6 rounded-xl border border-zinc-200 p-4">
      <p className="text-sm font-semibold text-zinc-900">Have a doubt? Ask the tutor</p>
      <p className="mt-0.5 text-xs text-zinc-500">
        Every answer says where it comes from. Asking does not affect your difficulty level.
      </p>

      {turns.length > 0 && (
        <ul className="mt-3 space-y-3" aria-live="polite">
          {turns.map((t, i) => (
            <li key={i} className="space-y-1.5 text-sm">
              <p className="ml-8 rounded-lg bg-zinc-900 px-3 py-2 text-white">{t.question}</p>
              <div className="mr-8 rounded-lg bg-zinc-100 px-3 py-2">
                <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-semibold ${SOURCE_STYLE[t.source] ?? SOURCE_STYLE.none}`}>
                  {t.label}
                </span>
                {t.answer && <p className="mt-1.5 whitespace-pre-wrap leading-relaxed text-zinc-800">{t.answer}</p>}
                {/* Provenance shown beside the answer, not only cited: the quote, file, page and upload date. */}
                {t.source === "notes" && t.sourceQuote && (
                  <blockquote className="mt-2 border-l-2 border-green-400 pl-3 text-xs leading-relaxed text-zinc-600">
                    <p>“{t.sourceQuote}”</p>
                    <p className="mt-1 text-zinc-500">
                      {t.fileName} · {t.pageLabel}
                      {t.uploadedAt && ` · uploaded ${formatDate(t.uploadedAt)}`}
                    </p>
                    {t.fromPictureDescription && (
                      <p className="mt-1 text-amber-800">
                        This quote is the tutor&apos;s own description of a picture in your notes, not the picture itself.
                      </p>
                    )}
                  </blockquote>
                )}
                {t.canAskGeneral && (
                  <button
                    onClick={() => send(t.question, true, i)}
                    disabled={disabled || asking}
                    className="mt-2 rounded-lg border border-zinc-300 bg-white px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
                  >
                    Answer from general knowledge anyway
                  </button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      {asking && <p className="mt-3 text-xs text-zinc-500">The tutor is writing an answer…</p>}
      {error && (
        <div className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-600">
          <p>{error}</p>
          <button
            onClick={() => send(draft.trim(), false)}
            disabled={asking || !draft.trim()}
            className="mt-1.5 rounded border border-red-300 px-2 py-0.5 font-medium hover:bg-red-100 disabled:opacity-50"
          >
            Retry
          </button>
        </div>
      )}

      <form onSubmit={ask} className="mt-3 flex gap-2">
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          maxLength={500}
          placeholder="Type your question about this lesson…"
          aria-label="Your question about this lesson"
          className="min-w-0 flex-1 rounded-lg border border-zinc-300 px-3 py-2 text-sm text-zinc-900 placeholder:text-zinc-400 focus:border-zinc-900 focus:outline-none"
        />
        <button
          type="submit"
          disabled={disabled || asking || !draft.trim()}
          className="rounded-lg border border-zinc-300 px-4 py-2 text-sm font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
        >
          Ask
        </button>
      </form>
    </div>
  );
}

function LessonView({
  step, busy, askUrl, onAction,
}: {
  step: Step;
  busy: boolean;
  askUrl: string;
  onAction: (a: "continue" | "explain" | "skip") => void;
}) {
  const lesson = step.lesson!;
  return (
    <div>
      {step.reason && <ReasonLabel text={step.reason} />}
      <h2 className="mb-3 text-lg font-semibold text-zinc-900">
        {step.phase === "review" ? "Review lesson" : `Lesson ${step.stepNumber}`}
      </h2>
      <p className="whitespace-pre-wrap text-sm leading-relaxed text-zinc-800">{lesson.text}</p>
      {lesson.example && (
        <div className="mt-4 rounded-xl border border-zinc-200 bg-zinc-50 px-4 py-3">
          <p className="mb-1 text-xs font-semibold uppercase tracking-wider text-zinc-500">Example</p>
          <p className="whitespace-pre-wrap text-sm leading-relaxed text-zinc-700">{lesson.example}</p>
        </div>
      )}
      {/* key: a new lesson (next step or a re-explanation) starts an empty chat. */}
      <LessonChat key={lesson.id} askUrl={askUrl} disabled={busy} saved={lesson.chat} />
      <div className="mt-6 flex flex-wrap gap-2">
        <button
          onClick={() => onAction("continue")}
          disabled={busy}
          className="rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50"
        >
          {step.phase === "review" ? "Take the re-test" : "Take the quiz"}
        </button>
        {step.phase === "lesson" && (
          <>
            <button
              onClick={() => onAction("explain")}
              disabled={busy}
              className="rounded-lg border border-zinc-300 px-4 py-2.5 text-sm font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
            >
              Explain differently
            </button>
            <button
              onClick={() => onAction("skip")}
              disabled={busy}
              className="rounded-lg border border-zinc-300 px-4 py-2.5 text-sm font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
            >
              Skip / I know this
            </button>
          </>
        )}
      </div>
    </div>
  );
}

function QuizView({
  step, busy, onSubmit, onSkip,
}: {
  step: Step;
  busy: boolean;
  onSubmit: (answers: number[], simulateWrong: boolean) => void;
  onSkip: () => void;
}) {
  const quiz = step.quiz!;
  // chosen[i] = index of the option picked for question i (undefined = not answered yet)
  const [chosen, setChosen] = useState<(number | undefined)[]>([]);
  const allAnswered = quiz.questions.every((_, i) => chosen[i] !== undefined);

  return (
    <div>
      {step.reason && <ReasonLabel text={step.reason} />}
      <h2 className="mb-1 text-lg font-semibold text-zinc-900">{QUIZ_TITLE[quiz.kind]}</h2>
      <p className="mb-4 text-xs text-zinc-500">
        {quiz.kind === "lesson"
          ? "You pass with at most one mistake."
          : "You need 60% or more on a topic; below that it is marked for review."}
      </p>

      <ol className="space-y-5">
        {quiz.questions.map((q, qi) => (
          <li key={q.id} className="rounded-xl border border-zinc-200 p-4">
            {q.label && (
              <span className="mb-2 inline-block rounded-full bg-purple-100 px-2 py-0.5 text-xs font-medium text-purple-700">
                {q.label}
              </span>
            )}
            <p className="mb-3 text-sm font-medium text-zinc-900">{qi + 1}. {q.text}</p>
            <div className="space-y-2">
              {q.options.map((opt, oi) => (
                <label
                  key={oi}
                  className={`flex cursor-pointer items-center gap-3 rounded-lg border px-3 py-2 text-sm ${
                    chosen[qi] === oi ? "border-zinc-900 bg-zinc-50" : "border-zinc-200 hover:border-zinc-400"
                  }`}
                >
                  <input
                    type="radio"
                    name={`q${q.id}`}
                    checked={chosen[qi] === oi}
                    onChange={() => setChosen((prev) => { const next = [...prev]; next[qi] = oi; return next; })}
                    className="accent-zinc-900"
                  />
                  <span className="text-zinc-800">{opt}</span>
                </label>
              ))}
            </div>
          </li>
        ))}
      </ol>

      <div className="mt-6 flex flex-wrap items-center gap-2">
        <button
          onClick={() => onSubmit(chosen as number[], false)}
          disabled={busy || !allAnswered}
          className="rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50"
        >
          Submit answers
        </button>
        {quiz.kind === "lesson" && (
          <button
            onClick={onSkip}
            disabled={busy}
            className="rounded-lg border border-zinc-300 px-4 py-2.5 text-sm font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
          >
            Skip / I know this
          </button>
        )}
        {/* Demo mode: lets the re-teach path be shown reliably in a presentation. */}
        <button
          onClick={() => onSubmit([], true)}
          disabled={busy}
          className="ml-auto rounded-lg border border-amber-300 bg-amber-50 px-4 py-2.5 text-sm font-medium text-amber-800 hover:bg-amber-100 disabled:opacity-50"
          title="Submits wrong answers on purpose so the adaptive path can be demonstrated."
        >
          Demo mode: submit wrong answers (simulated)
        </button>
      </div>
    </div>
  );
}

// The feedback screen shows a PREVIEW: nothing has taken effect yet. Here the
// learner can undo a slip, report a wrong question, and accept or refuse each
// level change. The decision is applied when they press the Next button.
function FeedbackView({
  step, result, busy, onSlip, onFlag, onFinish,
}: {
  step: Step;
  result: AnswerResult;
  busy: boolean;
  onSlip: (questionId: number, answer: number) => void;
  onFlag: (questionId: number) => void;
  onFinish: (keepLevelTopicIds: number[]) => void;
}) {
  const quiz = step.quiz!;
  // The learner's answer to each level proposal, by topic id. Next stays locked until all are answered.
  const [levelChoice, setLevelChoice] = useState<Record<number, "accept" | "keep">>({});
  // The question being answered again as a slip, and the option picked for it.
  const [slipFor, setSlipFor] = useState<number | null>(null);
  const [slipAnswer, setSlipAnswer] = useState<number | null>(null);

  const allChosen = result.levelProposals.every((p) => levelChoice[p.topicId] !== undefined);
  // Passed with exactly one mistake: that mistake is explained right in the summary box.
  const wrong = result.questions.filter((f) => !f.isCorrect && !f.flagged);
  const singleMistake = result.passed && wrong.length === 1 ? wrong[0] : null;
  const singleMistakeQuestion = singleMistake ? quiz.questions.find((q) => q.id === singleMistake.questionId) : null;
  const smallButton = "rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50";

  return (
    <div>
      <div className={`mb-4 rounded-xl border px-4 py-3 ${result.passed ? "border-green-200 bg-green-50" : "border-amber-200 bg-amber-50"}`}>
        <p className="text-sm font-semibold text-zinc-900">
          Score: {result.correct} / {result.total}
          {/* A short quiz is weak evidence, so the number of questions is always stated. */}
          <span className="ml-2 text-xs font-normal text-zinc-500">{result.evidence}</span>
          {result.simulated && (
            <span className="ml-2 rounded-full bg-amber-200 px-2 py-0.5 text-xs font-medium text-amber-900">
              simulated answers (demo mode)
            </span>
          )}
        </p>
        <p className="mt-1 text-sm text-zinc-700">{result.message}</p>
        {singleMistake && singleMistakeQuestion && (
          <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-zinc-800">
            <p className="font-medium">{singleMistakeQuestion.text}</p>
            <p className="mt-1">You chose: {singleMistakeQuestion.options[singleMistake.userAnswer] ?? "none"}</p>
            <p className="mt-1">
              <span className="font-semibold">What went wrong: </span>
              {singleMistake.whatWentWrong ?? singleMistake.explanation}
            </p>
            <p className="mt-1">Correct answer: {singleMistakeQuestion.options[singleMistake.correctIndex]}</p>
          </div>
        )}
        {result.insight && (
          <p className="mt-2 text-sm text-zinc-700">
            <span className="font-semibold">Insight: </span>{result.insight}
          </p>
        )}
        {result.levelNote && <p className="mt-2 text-xs text-zinc-600">{result.levelNote}</p>}
      </div>

      {/* Level changes are proposals: the cause is stated and the learner decides. */}
      {result.levelProposals.map((p) => (
        <div key={p.topicId} className="mb-4 rounded-xl border border-blue-200 bg-blue-50 px-4 py-3">
          <p className="text-sm text-blue-900">{p.message}</p>
          <p className="mt-0.5 text-xs text-blue-800">
            Level {p.oldLevel} → {p.newLevel} · {p.evidence}
          </p>
          <div className="mt-2 flex gap-2" role="group" aria-label={`Level change for ${p.topicTitle}`}>
            {(["accept", "keep"] as const).map((choice) => (
              <button
                key={choice}
                onClick={() => setLevelChoice((prev) => ({ ...prev, [p.topicId]: choice }))}
                aria-pressed={levelChoice[p.topicId] === choice}
                className={`rounded-lg border px-3 py-1.5 text-xs font-medium ${
                  levelChoice[p.topicId] === choice
                    ? "border-blue-700 bg-blue-700 text-white"
                    : "border-blue-300 bg-white text-blue-900 hover:bg-blue-100"
                }`}
              >
                {choice === "accept" ? `Accept (level ${p.newLevel})` : `Keep my level (${p.oldLevel})`}
              </button>
            ))}
          </div>
        </div>
      ))}

      {/* Checkpoints: score per topic */}
      {result.topicScores.length > 0 && (
        <div className="mb-4 rounded-xl border border-zinc-200 p-4">
          <p className="mb-2 text-xs font-semibold uppercase tracking-wider text-zinc-500">Score per topic</p>
          <ul className="space-y-1 text-sm">
            {result.topicScores.map((t) => (
              <li key={t.topicId} className="flex items-center justify-between gap-3">
                <span className="truncate text-zinc-700">{t.topicTitle}</span>
                <span className={`shrink-0 font-medium ${t.isWeak ? "text-amber-700" : "text-green-700"}`}>
                  {t.correct}/{t.total} ({t.scorePct}%){t.isWeak ? " · weak" : ""}
                  <span className="ml-2 font-normal text-zinc-500">{t.evidence}</span>
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* Per-answer feedback with the explanation */}
      <ol className="space-y-3">
        {quiz.questions.map((q, qi) => {
          const fb = result.questions.find((f) => f.questionId === q.id);
          if (!fb) return null;
          const border = fb.flagged ? "border-zinc-300 bg-zinc-50" : fb.isCorrect ? "border-green-200" : "border-red-200";
          return (
            <li key={q.id} className={`rounded-xl border p-4 text-sm ${border}`}>
              <p className="mb-2 font-medium text-zinc-900">
                {qi + 1}. {q.text}{" "}
                {fb.flagged ? (
                  <span className="text-zinc-500">Reported · not counted</span>
                ) : (
                  <span className={fb.isCorrect ? "text-green-700" : "text-red-600"}>
                    {fb.isCorrect ? "✓ Correct" : "✗ Not quite"}
                  </span>
                )}
                {fb.slip && <span className="ml-2 text-xs font-normal text-zinc-500">(answered again as a slip)</span>}
              </p>
              <p className="text-zinc-700">Your answer: {q.options[fb.userAnswer] ?? "none"}</p>
              {!fb.isCorrect && fb.whatWentWrong && (
                <p className="text-zinc-700"><span className="font-medium">What went wrong: </span>{fb.whatWentWrong}</p>
              )}
              {!fb.isCorrect && <p className="text-zinc-700">Correct answer: {q.options[fb.correctIndex]}</p>}
              {fb.explanation && <p className="mt-2 text-zinc-500">{fb.explanation}</p>}
              {/* The words of the uploaded notes the question rests on (checked by the server). */}
              {fb.sourceQuote && (
                <p className="mt-2 border-l-2 border-green-400 pl-3 text-xs text-zinc-600">
                  <span className="font-semibold">From your notes: </span>“{fb.sourceQuote}”
                </p>
              )}

              {/* Answering again after a slip: pick the option that was meant. */}
              {slipFor === q.id && (
                <div className="mt-3 rounded-lg border border-zinc-200 bg-white p-3">
                  <p className="mb-2 text-xs text-zinc-600">Pick the answer you meant. This does not count against you.</p>
                  <div className="space-y-1">
                    {q.options.map((opt, oi) => (
                      <label key={oi} className="flex cursor-pointer items-center gap-2 text-sm text-zinc-800">
                        <input type="radio" name={`slip${q.id}`} checked={slipAnswer === oi} onChange={() => setSlipAnswer(oi)} className="accent-zinc-900" />
                        {opt}
                      </label>
                    ))}
                  </div>
                  <div className="mt-2 flex gap-2">
                    <button
                      onClick={() => { onSlip(q.id, slipAnswer!); setSlipFor(null); setSlipAnswer(null); }}
                      disabled={busy || slipAnswer === null}
                      className={smallButton}
                    >
                      Save this answer
                    </button>
                    <button onClick={() => { setSlipFor(null); setSlipAnswer(null); }} className={smallButton}>Cancel</button>
                  </div>
                </div>
              )}

              {/* Error recovery: a slip gets an undo, a faulty question can be reported. */}
              {!fb.flagged && slipFor !== q.id && (
                <div className="mt-3 flex flex-wrap gap-2">
                  {!fb.isCorrect && result.slipAvailable && (
                    <button
                      onClick={() => { setSlipFor(q.id); setSlipAnswer(null); }}
                      disabled={busy}
                      className={smallButton}
                      title="You knew it but picked the wrong option by accident. Once per quiz."
                    >
                      That was a slip
                    </button>
                  )}
                  <button
                    onClick={() => onFlag(q.id)}
                    disabled={busy}
                    className={smallButton}
                    title="The question itself is wrong or unclear. It will not count and will not be used again."
                  >
                    Report a wrong question
                  </button>
                </div>
              )}
            </li>
          );
        })}
      </ol>

      <div className="mt-6 flex flex-wrap items-center gap-3">
        <button
          onClick={() => onFinish(result.levelProposals.filter((p) => levelChoice[p.topicId] === "keep").map((p) => p.topicId))}
          disabled={busy || !allChosen}
          className="rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50"
        >
          {NEXT_LABEL[result.decision] ?? "Continue"}
        </button>
        {!allChosen && <span className="text-xs text-zinc-500">Choose Accept or Keep my level above first.</span>}
      </div>
    </div>
  );
}

// ─── Page ────────────────────────────────────────────────────────────────────

export default function SessionPage() {
  // useParams needs a Suspense boundary in this Next.js version.
  return (
    <Suspense>
      <SessionContent />
    </Suspense>
  );
}

function SessionContent() {
  const { id: courseId, sessionId } = useParams<{ id: string; sessionId: string }>();
  const base = `/courses/${courseId}/sessions/${sessionId}`;

  const [view, setView] = useState<View>({ kind: "loading", text: "Preparing your lesson…" });
  const [busy, setBusy] = useState(false);
  // Header info is kept separately so it stays visible while the next step loads.
  const [header, setHeader] = useState<Step | null>(null);

  // Ask the server what to show now. The server owns the lesson state, so a
  // page refresh always resumes at the right place.
  const loadStep = useCallback(
    () =>
      apiService
        .get<Step>(`${base}/step`)
        .then((step) => {
          setHeader(step);
          setView({ kind: "step", step });
        })
        .catch((e: Error) => setView({ kind: "error", text: e.message }))
        .finally(() => setBusy(false)),
    [base]
  );

  useEffect(() => {
    loadStep();
  }, [loadStep]);

  function reload(text: string) {
    setView({ kind: "loading", text });
    loadStep();
  }

  // Lesson buttons: continue to quiz / explain differently / skip.
  async function act(action: "continue" | "explain" | "skip") {
    setBusy(true);
    try {
      await apiService.post(`${base}/${action}`, {});
      reload(action === "explain" ? "Writing a different explanation…" : "Loading…");
    } catch (e) {
      setView({ kind: "error", text: e instanceof Error ? e.message : "Something went wrong." });
      setBusy(false);
    }
  }

  async function submit(step: Step, answers: number[], simulateWrong: boolean) {
    setBusy(true);
    try {
      const result = await apiService.post<AnswerResult>(`${base}/quizzes/${step.quiz!.id}/answers`, { answers, simulateWrong });
      // This result is a preview: nothing takes effect until "Next" (finish below).
      setView({ kind: "feedback", step, result });
    } catch (e) {
      setView({ kind: "error", text: e instanceof Error ? e.message : "Something went wrong." });
    } finally {
      setBusy(false);
    }
  }

  // On the feedback screen: "That was a slip" and "Report a wrong question".
  // Both return the re-worked preview, which replaces the one on screen.
  async function review(step: Step, path: string, body: unknown) {
    setBusy(true);
    try {
      const result = await apiService.post<AnswerResult>(`${base}/quizzes/${step.quiz!.id}/${path}`, body);
      setView({ kind: "feedback", step, result });
    } catch (e) {
      setView({ kind: "error", text: e instanceof Error ? e.message : "Something went wrong." });
    } finally {
      setBusy(false);
    }
  }

  // "Next": the decision takes effect, except the level changes the learner chose to keep.
  async function finish(step: Step, keepLevelTopicIds: number[]) {
    setBusy(true);
    try {
      await apiService.post(`${base}/quizzes/${step.quiz!.id}/finish`, { keepLevelTopicIds });
      reload("Loading…");
    } catch (e) {
      setView({ kind: "error", text: e instanceof Error ? e.message : "Something went wrong." });
      setBusy(false);
    }
  }

  return (
    <div className="min-h-screen bg-zinc-50">
      <header className="border-b border-zinc-200 bg-white px-6 py-4">
        <div className="mx-auto max-w-3xl">
          <div className="mb-3 flex items-center gap-3">
            <Link href={`/courses/${courseId}/learn`} className="text-sm text-zinc-500 hover:text-zinc-800">← Topics</Link>
            <span className="text-zinc-300">/</span>
            <h1 className="flex-1 truncate text-sm font-semibold text-zinc-900">{header?.topicTitle ?? "…"}</h1>
            {header && header.level > 0 && (
              <span className="shrink-0 rounded-full bg-zinc-100 px-2.5 py-1 text-xs font-medium text-zinc-700" title="Difficulty adapts to your quiz results: 1 = easiest, 5 = hardest.">
                Level {header.level} / 5 · {header.styleLabel}
              </span>
            )}
            {header?.mockMode && <MockBadge />}
          </div>
          {/* A topic test has no lesson steps, so no progress bar. */}
          {header && header.totalSteps > 0 && <ProgressBar current={header.stepNumber} completed={header.completedSteps} total={header.totalSteps} />}
        </div>
      </header>

      <main className="mx-auto max-w-3xl px-6 py-8">
        <div className="rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm">
          {view.kind === "loading" && <p className="text-sm text-zinc-500">{view.text}</p>}

          {view.kind === "error" && (
            <div className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-600">
              <p>{view.text}</p>
              <button
                onClick={() => reload("Retrying…")}
                className="mt-2 rounded-lg border border-red-300 px-3 py-1 text-xs font-medium hover:bg-red-100"
              >
                Retry
              </button>
            </div>
          )}

          {view.kind === "step" && view.step.type === "lesson" && (
            <LessonView step={view.step} busy={busy} askUrl={`${base}/ask`} onAction={act} />
          )}

          {view.kind === "step" && view.step.type === "quiz" && (
            <QuizView
              key={view.step.quiz!.id}
              step={view.step}
              busy={busy}
              onSubmit={(answers, simulateWrong) => submit(view.step, answers, simulateWrong)}
              onSkip={() => act("skip")}
            />
          )}

          {view.kind === "feedback" && (
            <FeedbackView
              key={view.step.quiz!.id}
              step={view.step}
              result={view.result}
              busy={busy}
              onSlip={(questionId, answer) => review(view.step, `questions/${questionId}/slip`, { answer })}
              onFlag={(questionId) => review(view.step, `questions/${questionId}/flag`, {})}
              onFinish={(keep) => finish(view.step, keep)}
            />
          )}

          {view.kind === "step" && view.step.type === "done" && (
            <div>
              <h2 className="mb-2 text-lg font-semibold text-zinc-900">
                {view.step.totalSteps > 0 ? "Topic complete 🎉" : "Topic test complete"}
              </h2>
              <p className="text-sm text-zinc-700">{view.step.message}</p>
              <div className="mt-6 flex gap-2">
                <Link href={`/courses/${courseId}/learn`} className="rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700">
                  Back to topics
                </Link>
                <Link href={`/courses/${courseId}/history`} className="rounded-lg border border-zinc-300 px-5 py-2.5 text-sm font-medium text-zinc-700 hover:bg-zinc-50">
                  View history
                </Link>
              </div>
            </div>
          )}
        </div>
      </main>
    </div>
  );
}
