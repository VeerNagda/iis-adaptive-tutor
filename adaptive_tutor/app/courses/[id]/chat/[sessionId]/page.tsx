"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";

const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242";

// ── API shapes (backend: Steps 3-5) ──────────────────────────────────────────
//
// GET  /courses/{cid}/sessions/{sid}/next
//   → NextPayload  (initial load, or after "Continue" in teach phase)
// POST /courses/{cid}/sessions/{sid}/answer   body: AnswerBody
//   → AnswerResult (per-question submission; includes next thing when quiz ends)
// POST /courses/{cid}/sessions/{sid}/explain  (no body)
//   → NextPayload  (re-teach current section, simplified mode)
// POST /courses/{cid}/sessions/{sid}/skip     (no body)
//   → NextPayload  (skip to next section's teach content)

type Option = { id: string; text: string };

type NextPayload =
  | {
      type: "teach";
      content: string;        // ~150-word explanation
      label?: string;         // e.g. "Re-explained because you missed: X"
      sectionTitle: string;
      sectionIndex: number;   // 1-based
      totalSections: number;
      mode: "standard" | "simplified";
    }
  | {
      type: "quiz";
      question: {
        id: string;
        text: string;
        inputType: "text" | "single" | "multi";
        options?: Option[];   // present for single / multi
        concept?: string;     // tag shown on the question card
        label?: string;       // e.g. "Refresher from Section 2"
      };
      questionIndex: number;  // 1-based, within this section's quiz
      totalQuestions: number;
      sectionIndex: number;
      totalSections: number;
    }
  | { type: "done"; message: string; sectionIndex: number; totalSections: number };

type AnswerBody = { questionId: string; answers: string[] };

type AnswerResult = {
  correct: boolean;
  explanation: string;
  // present once the last question of a quiz is answered:
  quizDone?: true;
  next?: NextPayload;
};

// ── Chat message union ────────────────────────────────────────────────────────

type Msg =
  | { kind: "tutor";    id: string; text: string; label?: string }
  | { kind: "question"; id: string; text: string; concept?: string; label?: string;
      inputType: "text" | "single" | "multi"; options?: Option[] }
  | { kind: "student";  id: string; text: string; simulated?: true }
  | { kind: "feedback"; id: string; correct: boolean; explanation: string };

type Phase = "loading" | "teach" | "quiz" | "done" | "error";
type Progress = { section: number; total: number };

// ── API helpers ───────────────────────────────────────────────────────────────

async function fetchNext(cid: string, sid: string): Promise<NextPayload> {
  const r = await fetch(`${API}/courses/${cid}/sessions/${sid}/next`);
  if (!r.ok) throw new Error(await r.text());
  return r.json();
}

async function fetchExplain(cid: string, sid: string): Promise<NextPayload> {
  const r = await fetch(`${API}/courses/${cid}/sessions/${sid}/explain`, { method: "POST" });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
}

async function fetchSkip(cid: string, sid: string): Promise<NextPayload> {
  const r = await fetch(`${API}/courses/${cid}/sessions/${sid}/skip`, { method: "POST" });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
}

async function fetchAnswer(cid: string, sid: string, body: AnswerBody): Promise<AnswerResult> {
  const r = await fetch(`${API}/courses/${cid}/sessions/${sid}/answer`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!r.ok) throw new Error(await r.text());
  return r.json();
}

// ── Mock flow (used when the backend session endpoints are not yet available) ──
// Remove this once Steps 3-5 are implemented and /next is live.

type MockStep =
  | { action: "teach" }
  | { action: "quiz"; qIndex: number }
  | { action: "done" };

const MOCK_SECTIONS: NextPayload[] = [
  {
    type: "teach",
    sectionTitle: "What is Machine Learning?",
    sectionIndex: 1,
    totalSections: 3,
    mode: "standard",
    content:
      "Machine learning is a branch of artificial intelligence where systems learn from data rather than following explicit rules.\n\nInstead of programming every decision, we give the system examples and let it find patterns. For instance, a spam filter doesn't have a hand-coded list of banned words — it learns from thousands of labelled emails what spam looks like.\n\nThere are three main flavours: supervised learning (labelled examples), unsupervised learning (find structure on your own), and reinforcement learning (learn by trial and reward).",
  },
  {
    type: "quiz",
    sectionIndex: 1,
    totalSections: 3,
    questionIndex: 1,
    totalQuestions: 3,
    question: {
      id: "q1",
      text: "Which of the following best describes supervised learning?",
      inputType: "single",
      concept: "Supervised learning",
      options: [
        { id: "a", text: "The model finds hidden patterns without any labels." },
        { id: "b", text: "The model learns from labelled input–output pairs." },
        { id: "c", text: "The model improves by receiving rewards for actions." },
        { id: "d", text: "The model memorises the training set exactly." },
      ],
    },
  },
  {
    type: "quiz",
    sectionIndex: 1,
    totalSections: 3,
    questionIndex: 2,
    totalQuestions: 3,
    question: {
      id: "q2",
      text: "Name one real-world application of machine learning.",
      inputType: "text",
      concept: "Applications",
    },
  },
  {
    type: "quiz",
    sectionIndex: 1,
    totalSections: 3,
    questionIndex: 3,
    totalQuestions: 3,
    question: {
      id: "q3",
      text: "Which of the following are types of machine learning? (Select all that apply)",
      inputType: "multi",
      concept: "ML types",
      options: [
        { id: "a", text: "Supervised learning" },
        { id: "b", text: "Declarative learning" },
        { id: "c", text: "Unsupervised learning" },
        { id: "d", text: "Reinforcement learning" },
      ],
    },
  },
  {
    type: "teach",
    sectionTitle: "Overfitting and Underfitting",
    sectionIndex: 2,
    totalSections: 3,
    mode: "standard",
    content:
      "A model that memorises the training data too closely is said to overfit — it performs brilliantly on data it has seen but poorly on new data. Imagine a student who memorises past exam answers word-for-word but can't apply the concepts to a novel question.\n\nUnderfitting is the opposite: the model is too simple to capture the real pattern, like drawing a straight line through data that curves.\n\nThe goal is a model that generalises — finding the signal without chasing the noise.",
  },
  {
    type: "done",
    message: "Well done — you've completed all sections! Head back to the course to start a new session or upload more materials.",
    sectionIndex: 3,
    totalSections: 3,
  },
];

// Tracks position through the mock flow per session (resets on page refresh, which is fine for a demo)
const mockCursor: Record<string, number> = {};

function mockNext(sid: string): NextPayload {
  const i = mockCursor[sid] ?? 0;
  const step = MOCK_SECTIONS[Math.min(i, MOCK_SECTIONS.length - 1)];
  mockCursor[sid] = i + 1;
  return step;
}

function mockAnswer(qid: string, answers: string[]): AnswerResult {
  const correct = qid === "q1" ? answers[0] === "b"
    : qid === "q3" ? answers.includes("a") && answers.includes("c") && answers.includes("d") && !answers.includes("b")
    : answers[0].trim().length > 3; // text: anything non-trivial
  return {
    correct,
    explanation: correct
      ? "Correct! That's exactly right."
      : "Not quite — review the section above and try to recall the key idea before moving on.",
  };
}

// ── Misc helpers ──────────────────────────────────────────────────────────────

function uid() { return Math.random().toString(36).slice(2); }

// ── Input components ──────────────────────────────────────────────────────────

function TextInput({ onSubmit, disabled }: { onSubmit: (v: string) => void; disabled: boolean }) {
  const [value, setValue] = useState("");
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => { ref.current?.focus(); }, []);

  return (
    <div className="flex items-end gap-2">
      <textarea
        ref={ref}
        rows={2}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        disabled={disabled}
        placeholder="Type your answer…"
        className="flex-1 resize-none rounded-xl border border-zinc-300 px-3 py-2.5 text-sm text-zinc-900 placeholder-zinc-400 focus:outline-none focus:ring-2 focus:ring-zinc-900 transition disabled:opacity-50"
        onKeyDown={(e) => {
          if (e.key === "Enter" && !e.shiftKey && value.trim()) {
            e.preventDefault();
            onSubmit(value.trim());
            setValue("");
          }
        }}
      />
      <button
        onClick={() => { if (value.trim()) { onSubmit(value.trim()); setValue(""); } }}
        disabled={disabled || !value.trim()}
        className="shrink-0 rounded-xl bg-zinc-900 px-4 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-40 transition-colors"
      >
        Submit
      </button>
    </div>
  );
}

function SingleChoice({
  options, onSubmit, disabled,
}: { options: Option[]; onSubmit: (id: string) => void; disabled: boolean }) {
  const [selected, setSelected] = useState<string | null>(null);
  return (
    <div className="space-y-2">
      {options.map((opt) => (
        <label
          key={opt.id}
          className={`flex cursor-pointer select-none items-center gap-3 rounded-xl border px-4 py-3 transition-colors ${
            selected === opt.id ? "border-zinc-900 bg-zinc-50" : "border-zinc-200 hover:border-zinc-400 hover:bg-zinc-50"
          } ${disabled ? "pointer-events-none opacity-50" : ""}`}
        >
          <input
            type="radio"
            name="single"
            value={opt.id}
            checked={selected === opt.id}
            onChange={() => setSelected(opt.id)}
            disabled={disabled}
            className="accent-zinc-900 shrink-0"
          />
          <span className="text-sm text-zinc-800">{opt.text}</span>
        </label>
      ))}
      <button
        onClick={() => selected && onSubmit(selected)}
        disabled={disabled || !selected}
        className="w-full rounded-xl bg-zinc-900 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-40 transition-colors"
      >
        Submit answer
      </button>
    </div>
  );
}

function MultiChoice({
  options, onSubmit, disabled,
}: { options: Option[]; onSubmit: (ids: string[]) => void; disabled: boolean }) {
  const [selected, setSelected] = useState<Set<string>>(new Set());

  function toggle(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  return (
    <div className="space-y-2">
      <p className="text-xs text-zinc-400">Select all that apply</p>
      {options.map((opt) => (
        <label
          key={opt.id}
          className={`flex cursor-pointer select-none items-center gap-3 rounded-xl border px-4 py-3 transition-colors ${
            selected.has(opt.id) ? "border-zinc-900 bg-zinc-50" : "border-zinc-200 hover:border-zinc-400 hover:bg-zinc-50"
          } ${disabled ? "pointer-events-none opacity-50" : ""}`}
        >
          <input
            type="checkbox"
            value={opt.id}
            checked={selected.has(opt.id)}
            onChange={() => toggle(opt.id)}
            disabled={disabled}
            className="accent-zinc-900 shrink-0"
          />
          <span className="text-sm text-zinc-800">{opt.text}</span>
        </label>
      ))}
      <button
        onClick={() => selected.size > 0 && onSubmit([...selected])}
        disabled={disabled || selected.size === 0}
        className="w-full rounded-xl bg-zinc-900 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-40 transition-colors"
      >
        Submit answer
      </button>
    </div>
  );
}

// ── Chat bubble components ────────────────────────────────────────────────────

function TutorBubble({ text, label }: { text: string; label?: string }) {
  return (
    <div className="flex max-w-[80%] flex-col gap-1">
      {label && <span className="px-1 text-xs text-zinc-400">{label}</span>}
      <div className="rounded-2xl rounded-tl-sm border border-zinc-200 bg-white px-4 py-3 text-sm leading-relaxed text-zinc-800 shadow-sm whitespace-pre-wrap">
        {text}
      </div>
    </div>
  );
}

function QuestionCard({ text, concept, label }: { text: string; concept?: string; label?: string }) {
  return (
    <div className="flex max-w-[85%] flex-col gap-1">
      {label && <span className="px-1 text-xs text-zinc-400">{label}</span>}
      <div className="rounded-2xl rounded-tl-sm bg-zinc-900 px-4 py-3 text-sm text-white shadow-sm">
        {concept && (
          <span className="mb-2 inline-block rounded-full bg-zinc-700 px-2 py-0.5 text-xs font-medium text-zinc-300">
            {concept}
          </span>
        )}
        <p className="leading-relaxed">{text}</p>
      </div>
    </div>
  );
}

function StudentBubble({ text, simulated }: { text: string; simulated?: true }) {
  return (
    <div className="flex justify-end">
      <div className="max-w-[70%] rounded-2xl rounded-tr-sm border border-zinc-200 bg-zinc-100 px-4 py-3 text-sm leading-relaxed text-zinc-800">
        {text}
        {simulated && (
          <span className="ml-2 rounded-full bg-amber-200 px-1.5 py-0.5 text-xs font-medium text-amber-800">
            simulated
          </span>
        )}
      </div>
    </div>
  );
}

function FeedbackBubble({ correct, explanation }: { correct: boolean; explanation: string }) {
  return (
    <div
      className={`flex max-w-[80%] flex-col gap-1 rounded-2xl rounded-tl-sm border px-4 py-3 text-sm leading-relaxed shadow-sm ${
        correct ? "border-green-200 bg-green-50 text-green-900" : "border-red-200 bg-red-50 text-red-900"
      }`}
    >
      <div className="flex items-center gap-1.5 font-semibold">
        {correct ? (
          <>
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
            </svg>
            Correct!
          </>
        ) : (
          <>
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
            Not quite
          </>
        )}
      </div>
      <p>{explanation}</p>
    </div>
  );
}

function ProgressBar({ current, total }: { current: number; total: number }) {
  const pct = total > 0 ? Math.round((current / total) * 100) : 0;
  return (
    <div className="flex items-center gap-3">
      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-zinc-200">
        <div
          className="h-full rounded-full bg-zinc-900 transition-all duration-500"
          style={{ width: `${pct}%` }}
        />
      </div>
      <span className="shrink-0 text-xs text-zinc-500">
        Section {current} / {total}
      </span>
    </div>
  );
}

function Spinner() {
  return (
    <svg className="h-4 w-4 animate-spin" fill="none" viewBox="0 0 24 24">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
    </svg>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function SessionPage() {
  return (
    <Suspense>
      <SessionContent />
    </Suspense>
  );
}

function SessionContent() {
  const { id: courseId, sessionId } = useParams<{ id: string; sessionId: string }>();

  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [phase, setPhase] = useState<Phase>("loading");
  const [progress, setProgress] = useState<Progress>({ section: 0, total: 0 });

  // current quiz question waiting for an answer
  const [pending, setPending] = useState<{
    id: string;
    inputType: "text" | "single" | "multi";
    options?: Option[];
  } | null>(null);

  const [submitting, setSubmitting] = useState(false);
  const [demoMode, setDemoMode] = useState(false);
  const [error, setError] = useState("");
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [msgs]);

  useEffect(() => { load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  function push(m: Msg) { setMsgs((prev) => [...prev, m]); }

  function applyNext(payload: NextPayload) {
    if (payload.type === "teach") {
      setProgress({ section: payload.sectionIndex, total: payload.totalSections });
      setPhase("teach");
      push({ kind: "tutor", id: uid(), text: payload.content, label: payload.label });
      setPending(null);
    } else if (payload.type === "quiz") {
      setProgress({ section: payload.sectionIndex, total: payload.totalSections });
      setPhase("quiz");
      const q = payload.question;
      push({
        kind: "question",
        id: uid(),
        text: q.text,
        concept: q.concept,
        label: q.label,
        inputType: q.inputType,
        options: q.options,
      });
      setPending({ id: q.id, inputType: q.inputType, options: q.options });
    } else {
      setPhase("done");
      push({ kind: "tutor", id: uid(), text: payload.message });
      setPending(null);
    }
  }

  // Calls the real backend; falls back to mock data when the endpoint isn't ready yet.
  async function callNext(): Promise<NextPayload> {
    try {
      return await fetchNext(courseId, sessionId);
    } catch {
      return mockNext(sessionId);
    }
  }

  async function load() {
    setError("");
    setSubmitting(true);
    try {
      applyNext(await callNext());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load session.");
      setPhase("error");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleContinue() {
    setSubmitting(true);
    setError("");
    try {
      applyNext(await callNext());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to continue.");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleExplain() {
    setSubmitting(true);
    setError("");
    try {
      // fall back to mock: re-show current teach content with a simplified label
      let payload: NextPayload;
      try { payload = await fetchExplain(courseId, sessionId); }
      catch {
        const base = MOCK_SECTIONS.find(
          (s): s is Extract<NextPayload, { type: "teach" }> => s.type === "teach"
        )!;
        payload = { ...base, mode: "simplified", label: "Re-explained in simpler terms" };
      }
      applyNext(payload);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to request re-explanation.");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleSkip() {
    setSubmitting(true);
    setError("");
    push({ kind: "student", id: uid(), text: "Skip — I know this." });
    try {
      let payload: NextPayload;
      try { payload = await fetchSkip(courseId, sessionId); }
      catch { payload = mockNext(sessionId); }
      applyNext(payload);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to skip section.");
    } finally {
      setSubmitting(false);
    }
  }

  async function handleAnswer(answers: string[], display: string, simulated?: true) {
    if (!pending) return;
    setSubmitting(true);
    setError("");
    push({ kind: "student", id: uid(), text: display, simulated });
    try {
      let result: AnswerResult;
      try { result = await fetchAnswer(courseId, sessionId, { questionId: pending.id, answers }); }
      catch { result = mockAnswer(pending.id, answers); }

      push({ kind: "feedback", id: uid(), correct: result.correct, explanation: result.explanation });
      if (result.next) applyNext(result.next);
      else applyNext(await callNext());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to submit answer.");
    } finally {
      setSubmitting(false);
    }
  }

  // Demo: submit a deliberately wrong answer to trigger the re-teach path
  function handleDemoAnswer() {
    if (!pending) return;
    if (pending.inputType === "text") {
      handleAnswer(["I don't know"], "I don't know", true);
    } else if (pending.inputType === "single" && pending.options?.length) {
      const wrong = pending.options[pending.options.length - 1];
      handleAnswer([wrong.id], wrong.text, true);
    } else if (pending.inputType === "multi" && pending.options?.length) {
      const wrong = pending.options[0];
      handleAnswer([wrong.id], wrong.text, true);
    }
  }

  // ── Render ──────────────────────────────────────────────────────────────────

  return (
    <div className="flex h-screen flex-col bg-zinc-50">

      {/* ── Header ── */}
      <header className="shrink-0 border-b border-zinc-200 bg-white px-4 py-3">
        <div className="mx-auto flex max-w-3xl items-center gap-3">
          <Link
            href={`/courses/${courseId}/chat`}
            className="shrink-0 rounded-lg p-1.5 text-zinc-400 transition-colors hover:bg-zinc-100 hover:text-zinc-700"
            title="Back to course"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
            </svg>
          </Link>

          <div className="min-w-0 flex-1">
            {progress.total > 0 ? (
              <ProgressBar current={progress.section} total={progress.total} />
            ) : (
              <div className="h-1.5 animate-pulse rounded-full bg-zinc-200" />
            )}
          </div>

          {/* Demo mode toggle */}
          <button
            onClick={() => setDemoMode((v) => !v)}
            title="Demo mode — submits scripted wrong answers to demonstrate the re-teach path (simulated)"
            className={`shrink-0 rounded-lg border px-3 py-1.5 text-xs font-medium transition-colors ${
              demoMode
                ? "border-amber-300 bg-amber-50 text-amber-700"
                : "border-zinc-200 text-zinc-500 hover:border-zinc-300 hover:bg-zinc-50"
            }`}
          >
            Demo mode
          </button>
        </div>
      </header>

      {/* ── Chat scroll area ── */}
      <main className="flex-1 overflow-y-auto px-4 py-6">
        <div className="mx-auto max-w-3xl space-y-4">

          {phase === "loading" && msgs.length === 0 && (
            <div className="space-y-3 py-4">
              <div className="h-4 w-2/3 animate-pulse rounded bg-zinc-200" />
              <div className="h-4 w-full animate-pulse rounded bg-zinc-200" />
              <div className="h-4 w-5/6 animate-pulse rounded bg-zinc-200" />
            </div>
          )}

          {msgs.map((m) => {
            switch (m.kind) {
              case "tutor":    return <TutorBubble key={m.id} text={m.text} label={m.label} />;
              case "question": return <QuestionCard key={m.id} text={m.text} concept={m.concept} label={m.label} />;
              case "student":  return <StudentBubble key={m.id} text={m.text} simulated={m.simulated} />;
              case "feedback": return <FeedbackBubble key={m.id} correct={m.correct} explanation={m.explanation} />;
            }
          })}

          {error && (
            <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-600">
              {error}{" "}
              <button onClick={load} className="font-medium underline hover:no-underline">
                Retry
              </button>
            </div>
          )}

          <div ref={bottomRef} />
        </div>
      </main>

      {/* ── Action bar ── */}
      {phase !== "loading" && phase !== "error" && (
        <footer className="shrink-0 border-t border-zinc-200 bg-white px-4 py-4">
          <div className="mx-auto max-w-3xl space-y-3">

            {/* Teach phase */}
            {phase === "teach" && (
              <>
                {demoMode && (
                  <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700">
                    Demo mode — the quiz that follows will auto-submit wrong answers to show the re-teach path.
                  </p>
                )}
                <div className="flex flex-wrap gap-2">
                  <button
                    onClick={handleExplain}
                    disabled={submitting}
                    className="rounded-xl border border-zinc-300 px-4 py-2.5 text-sm font-medium text-zinc-700 transition-colors hover:bg-zinc-50 disabled:opacity-40"
                  >
                    Explain differently
                  </button>
                  <button
                    onClick={handleSkip}
                    disabled={submitting}
                    className="rounded-xl border border-zinc-300 px-4 py-2.5 text-sm font-medium text-zinc-700 transition-colors hover:bg-zinc-50 disabled:opacity-40"
                  >
                    Skip — I know this
                  </button>
                  <button
                    onClick={handleContinue}
                    disabled={submitting}
                    className="ml-auto flex items-center gap-1.5 rounded-xl bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-zinc-700 disabled:opacity-40"
                  >
                    {submitting ? <Spinner /> : (
                      <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M13 7l5 5m0 0l-5 5m5-5H6" />
                      </svg>
                    )}
                    Continue to quiz
                  </button>
                </div>
              </>
            )}

            {/* Quiz phase */}
            {phase === "quiz" && pending && (
              <>
                {demoMode && (
                  <div className="flex items-center justify-between rounded-lg border border-amber-200 bg-amber-50 px-3 py-2">
                    <span className="text-xs text-amber-700">Demo mode — answers are simulated</span>
                    <button
                      onClick={handleDemoAnswer}
                      disabled={submitting}
                      className="rounded-lg border border-amber-300 bg-amber-100 px-3 py-1 text-xs font-medium text-amber-800 transition-colors hover:bg-amber-200 disabled:opacity-40"
                    >
                      Submit wrong answer
                    </button>
                  </div>
                )}

                {pending.inputType === "text" && (
                  <TextInput
                    onSubmit={(v) => handleAnswer([v], v)}
                    disabled={submitting}
                  />
                )}

                {pending.inputType === "single" && pending.options && (
                  <SingleChoice
                    options={pending.options}
                    onSubmit={(id) => {
                      const label = pending.options!.find((o) => o.id === id)?.text ?? id;
                      handleAnswer([id], label);
                    }}
                    disabled={submitting}
                  />
                )}

                {pending.inputType === "multi" && pending.options && (
                  <MultiChoice
                    options={pending.options}
                    onSubmit={(ids) => {
                      const label = ids
                        .map((id) => pending.options!.find((o) => o.id === id)?.text ?? id)
                        .join(", ");
                      handleAnswer(ids, label);
                    }}
                    disabled={submitting}
                  />
                )}
              </>
            )}

            {/* Done */}
            {phase === "done" && (
              <Link
                href={`/courses/${courseId}/chat`}
                className="flex w-full items-center justify-center gap-2 rounded-xl bg-zinc-900 py-3 text-sm font-medium text-white transition-colors hover:bg-zinc-700"
              >
                Back to course
              </Link>
            )}
          </div>
        </footer>
      )}
    </div>
  );
}
