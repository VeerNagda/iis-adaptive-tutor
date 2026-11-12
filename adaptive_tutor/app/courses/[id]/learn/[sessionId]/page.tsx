"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useState } from "react";
import { MockBadge } from "../../../../components/MockBadge";
import { apiService } from "../../../../services/apiService";

// ─── Types (match the backend's Models/Learning.cs) ──────────────────────────

type Question = { id: number; text: string; options: string[]; label: string | null };
type Quiz = { id: number; kind: "lesson" | "checkpoint" | "retest"; questions: Question[] };

type Step = {
  type: "lesson" | "quiz" | "done";
  phase: string;
  mockMode: boolean;
  topicTitle: string;
  level: number; // difficulty 1..5
  styleLabel: string;
  stepNumber: number;
  totalSteps: number;
  reason: string | null; // why this content is shown
  lesson: { id: number; text: string; example: string | null } | null;
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
  questions: { questionId: number; userAnswer: number; correctIndex: number; isCorrect: boolean; explanation: string | null }[];
  topicScores: { topicId: number; topicTitle: string; correct: number; total: number; scorePct: number; isWeak: boolean }[];
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
};

const NEXT_LABEL: Record<AnswerResult["decision"], string> = {
  advance: "Next lesson",
  reteach: "See the simpler explanation",
  checkpoint: "Start the checkpoint test",
  review: "Start the review",
  done: "Finish",
};

// ─── Small pieces ────────────────────────────────────────────────────────────

function ProgressBar({ current, total }: { current: number; total: number }) {
  const pct = total > 0 ? Math.round((current / total) * 100) : 0;
  return (
    <div className="flex items-center gap-3">
      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-zinc-200">
        <div className="h-full rounded-full bg-zinc-900 transition-all duration-500" style={{ width: `${pct}%` }} />
      </div>
      <span className="shrink-0 text-xs text-zinc-500">Lesson {current} of {total}</span>
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

function LessonView({ step, busy, onAction }: { step: Step; busy: boolean; onAction: (a: "continue" | "explain" | "skip") => void }) {
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

function FeedbackView({ step, result, busy, onNext }: { step: Step; result: AnswerResult; busy: boolean; onNext: () => void }) {
  const quiz = step.quiz!;
  return (
    <div>
      <div className={`mb-4 rounded-xl border px-4 py-3 ${result.passed ? "border-green-200 bg-green-50" : "border-amber-200 bg-amber-50"}`}>
        <p className="text-sm font-semibold text-zinc-900">
          Score: {result.correct} / {result.total}
          {result.simulated && (
            <span className="ml-2 rounded-full bg-amber-200 px-2 py-0.5 text-xs font-medium text-amber-900">
              simulated answers (demo mode)
            </span>
          )}
        </p>
        <p className="mt-1 text-sm text-zinc-700">{result.message}</p>
        {result.insight && (
          <p className="mt-2 text-sm text-zinc-700">
            <span className="font-semibold">Insight: </span>{result.insight}
          </p>
        )}
        {result.newLevel !== result.oldLevel && (
          <p className="mt-2 text-xs text-zinc-600">
            Difficulty level changed from {result.oldLevel} to {result.newLevel} (
            {result.newLevel > result.oldLevel ? "harder, because you answered everything correctly" : "easier, to help you get there"}).
          </p>
        )}
      </div>

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
          return (
            <li key={q.id} className={`rounded-xl border p-4 text-sm ${fb.isCorrect ? "border-green-200" : "border-red-200"}`}>
              <p className="mb-2 font-medium text-zinc-900">
                {qi + 1}. {q.text}{" "}
                <span className={fb.isCorrect ? "text-green-700" : "text-red-600"}>
                  {fb.isCorrect ? "✓ Correct" : "✗ Not quite"}
                </span>
              </p>
              <p className="text-zinc-700">Your answer: {q.options[fb.userAnswer] ?? "none"}</p>
              {!fb.isCorrect && <p className="text-zinc-700">Correct answer: {q.options[fb.correctIndex]}</p>}
              {fb.explanation && <p className="mt-2 text-zinc-500">{fb.explanation}</p>}
            </li>
          );
        })}
      </ol>

      <button
        onClick={onNext}
        disabled={busy}
        className="mt-6 rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50"
      >
        {NEXT_LABEL[result.decision] ?? "Continue"}
      </button>
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
      setView({ kind: "feedback", step, result });
      setHeader((h) => (h ? { ...h, level: result.newLevel } : h));
    } catch (e) {
      setView({ kind: "error", text: e instanceof Error ? e.message : "Something went wrong." });
    } finally {
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
            {header && (
              <span className="shrink-0 rounded-full bg-zinc-100 px-2.5 py-1 text-xs font-medium text-zinc-700" title="Difficulty adapts to your quiz results: 1 = easiest, 5 = hardest.">
                Level {header.level} / 5 · {header.styleLabel}
              </span>
            )}
            {header?.mockMode && <MockBadge />}
          </div>
          {header && <ProgressBar current={header.stepNumber} total={header.totalSteps} />}
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
            <LessonView step={view.step} busy={busy} onAction={act} />
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
            <FeedbackView step={view.step} result={view.result} busy={busy} onNext={() => reload("Loading…")} />
          )}

          {view.kind === "step" && view.step.type === "done" && (
            <div>
              <h2 className="mb-2 text-lg font-semibold text-zinc-900">Topic complete 🎉</h2>
              <p className="text-sm text-zinc-700">{view.step.message}</p>
              <div className="mt-6 flex gap-2">
                <Link href={`/courses/${courseId}/learn`} className="rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700">
                  Back to topics
                </Link>
              </div>
            </div>
          )}
        </div>
      </main>
    </div>
  );
}
