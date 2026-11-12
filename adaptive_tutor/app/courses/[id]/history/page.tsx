"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { apiService } from "../../../services/apiService";

// ─── Types (match the backend's Models/Learning.cs) ──────────────────────────

type SessionHistory = {
  id: number;
  topicTitle: string;
  status: string;
  startedAt: string;
  endedAt: string | null;
  lessons: number;
  quizzes: number;
  quizzesPassed: number;
};

type TopicProgress = {
  id: number;
  title: string;
  status: string;
  level: number;
  lastCheckpointPct: number | null;
  levelChanges: { oldLevel: number; newLevel: number; reason: string; at: string }[];
};

type History = { sessions: SessionHistory[]; topics: TopicProgress[] };

const STATUS_STYLE: Record<string, string> = {
  new: "bg-zinc-100 text-zinc-600",
  learning: "bg-blue-100 text-blue-700",
  active: "bg-blue-100 text-blue-700",
  completed: "bg-green-100 text-green-700",
  weak: "bg-amber-100 text-amber-800",
};

// SQLite stores UTC timestamps as "YYYY-MM-DD HH:MM:SS"; show them in local time.
function formatTime(raw: string) {
  const d = new Date(raw.replace(" ", "T") + "Z");
  if (isNaN(d.getTime())) return raw;
  return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function StatusBadge({ status }: { status: string }) {
  return (
    <span className={`rounded-full px-2 py-0.5 text-xs font-medium capitalize ${STATUS_STYLE[status] ?? STATUS_STYLE.new}`}>
      {status}
    </span>
  );
}

export default function HistoryPage() {
  // useParams needs a Suspense boundary in this Next.js version.
  return (
    <Suspense>
      <HistoryContent />
    </Suspense>
  );
}

function HistoryContent() {
  const { id: courseId } = useParams<{ id: string }>();
  const [history, setHistory] = useState<History | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    apiService
      .get<History>(`/courses/${courseId}/history`)
      .then(setHistory)
      .catch((e: Error) => setError(e.message));
  }, [courseId]);

  return (
    <div className="min-h-screen bg-zinc-50">
      <header className="border-b border-zinc-200 bg-white px-6 py-4">
        <div className="mx-auto flex max-w-5xl items-center gap-4">
          <Link href={`/courses/${courseId}/learn`} className="text-sm text-zinc-500 hover:text-zinc-800">← Topics</Link>
          <span className="text-zinc-300">/</span>
          <h1 className="text-sm font-semibold text-zinc-900">History</h1>
        </div>
      </header>

      <main className="mx-auto max-w-5xl space-y-6 px-6 py-8">
        {error && <p className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-600">{error}</p>}
        {!history && !error && <p className="text-sm text-zinc-500">Loading…</p>}

        {history && (
          <>
            {/* Per-topic progress and difficulty */}
            <section className="rounded-2xl border border-zinc-200 bg-white p-6">
              <h2 className="mb-4 text-xs font-semibold uppercase tracking-wider text-zinc-500">Topic progress and difficulty</h2>
              {history.topics.length === 0 ? (
                <p className="text-sm text-zinc-400">No topics yet.</p>
              ) : (
                <ul className="space-y-4">
                  {history.topics.map((t) => (
                    <li key={t.id} className="rounded-xl border border-zinc-200 p-4">
                      <div className="flex flex-wrap items-center gap-2">
                        <h3 className="mr-auto text-sm font-semibold text-zinc-900">{t.title}</h3>
                        <StatusBadge status={t.status} />
                        <span className="rounded-full bg-zinc-100 px-2 py-0.5 text-xs font-medium text-zinc-700">
                          Level {t.level} / 5
                        </span>
                        <span className="text-xs text-zinc-500">
                          Last checkpoint: {t.lastCheckpointPct === null ? "none yet" : `${t.lastCheckpointPct}%`}
                        </span>
                      </div>
                      {t.levelChanges.length > 0 && (
                        <ul className="mt-3 space-y-1 border-t border-zinc-100 pt-3 text-xs text-zinc-600">
                          {t.levelChanges.map((c, i) => (
                            <li key={i}>
                              <span className="text-zinc-400">{formatTime(c.at)}</span>{" "}
                              Level {c.oldLevel} → {c.newLevel}{" "}
                              <span className={c.newLevel > c.oldLevel ? "text-green-700" : "text-amber-700"}>
                                ({c.newLevel > c.oldLevel ? "harder" : "easier"})
                              </span>
                              : {c.reason}
                            </li>
                          ))}
                        </ul>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </section>

            {/* Past sessions */}
            <section className="rounded-2xl border border-zinc-200 bg-white p-6">
              <h2 className="mb-4 text-xs font-semibold uppercase tracking-wider text-zinc-500">Sessions</h2>
              {history.sessions.length === 0 ? (
                <p className="text-sm text-zinc-400">No sessions yet. Pick a topic to start learning.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="text-xs text-zinc-500">
                      <tr>
                        <th className="pb-2 pr-4 font-medium">Started</th>
                        <th className="pb-2 pr-4 font-medium">Topic</th>
                        <th className="pb-2 pr-4 font-medium">Status</th>
                        <th className="pb-2 pr-4 font-medium">Lessons shown</th>
                        <th className="pb-2 pr-4 font-medium">Quizzes passed</th>
                        <th className="pb-2 font-medium"></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-zinc-100">
                      {history.sessions.map((s) => (
                        <tr key={s.id}>
                          <td className="py-2 pr-4 text-zinc-500">{formatTime(s.startedAt)}</td>
                          <td className="py-2 pr-4 text-zinc-800">{s.topicTitle}</td>
                          <td className="py-2 pr-4"><StatusBadge status={s.status} /></td>
                          <td className="py-2 pr-4 text-zinc-700">{s.lessons}</td>
                          <td className="py-2 pr-4 text-zinc-700">{s.quizzesPassed} / {s.quizzes}</td>
                          <td className="py-2 text-right">
                            {s.status === "active" && (
                              <Link href={`/courses/${courseId}/learn/${s.id}`} className="text-xs font-medium text-zinc-900 underline">
                                Resume
                              </Link>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          </>
        )}
      </main>
    </div>
  );
}
