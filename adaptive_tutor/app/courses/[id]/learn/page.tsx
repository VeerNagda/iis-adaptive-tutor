"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { MockBadge } from "../../../components/MockBadge";
import { apiService } from "../../../services/apiService";

// ─── Types (match the backend's Models/Learning.cs) ──────────────────────────

type Course = { id: number; title: string; description: string | null };
type Document = { id: number; filename: string; fileType: string; processingStatus: string };
type Topic = {
  id: number;
  title: string;
  summary: string | null;
  difficultyLevel: number; // 1 (easiest) .. 5 (hardest)
  status: "new" | "learning" | "completed" | "weak";
};
type TopicList = { mockMode: boolean; hasContent: boolean; topics: Topic[] };

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242";

const DOC_STATUS: Record<string, { label: string; cls: string }> = {
  pending: { label: "Waiting for ingestion", cls: "text-zinc-400" },
  processing: { label: "Processing…", cls: "text-blue-500" },
  completed: { label: "Ready", cls: "text-green-600" },
  failed: { label: "Ingestion failed", cls: "text-red-500" },
};

const TOPIC_STATUS: Record<Topic["status"], { label: string; cls: string; action: string }> = {
  new: { label: "Not started", cls: "bg-zinc-100 text-zinc-600", action: "Start" },
  learning: { label: "In progress", cls: "bg-blue-100 text-blue-700", action: "Continue" },
  completed: { label: "Completed", cls: "bg-green-100 text-green-700", action: "Learn again" },
  weak: { label: "Weak: review recommended", cls: "bg-amber-100 text-amber-800", action: "Review" },
};

// ─── Materials: the uploaded files the topics are extracted from ─────────────

function MaterialsPanel({ courseId }: { courseId: string }) {
  const [docs, setDocs] = useState<Document[]>([]);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState("");
  const fileInputRef = useRef<HTMLInputElement>(null);

  const fetchDocs = useCallback(
    () =>
      apiService
        .get<Document[]>(`/courses/${courseId}/documents`)
        .then(setDocs)
        .catch(() => setError("Failed to load materials.")),
    [courseId]
  );

  useEffect(() => {
    fetchDocs();
  }, [fetchDocs]);

  async function handleFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    setUploading(true);
    setError("");
    for (const file of Array.from(files)) {
      const form = new FormData();
      form.append("file", file);
      const res = await fetch(`${BASE}/courses/${courseId}/documents`, { method: "POST", body: form });
      if (!res.ok) setError(`Failed to upload ${file.name}`);
    }
    await fetchDocs();
    setUploading(false);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  async function handleRemove(docId: number) {
    try {
      await apiService.delete(`/courses/${courseId}/documents/${docId}`);
      setDocs((prev) => prev.filter((d) => d.id !== docId));
    } catch {
      setError("Failed to remove the file.");
    }
  }

  return (
    <section className="rounded-2xl border border-zinc-200 bg-white p-6">
      <div className="mb-4 flex items-center justify-between">
        <h2 className="text-xs font-semibold uppercase tracking-wider text-zinc-500">Materials</h2>
        <button
          onClick={() => fileInputRef.current?.click()}
          disabled={uploading}
          className="rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
        >
          {uploading ? "Uploading…" : "+ Add files"}
        </button>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          accept=".pdf,.pptx,.docx"
          className="hidden"
          onChange={(e) => handleFiles(e.target.files)}
        />
      </div>

      {error && <p className="mb-3 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-600">{error}</p>}

      {docs.length === 0 ? (
        <p className="text-sm text-zinc-400">No files yet. Upload a PDF, PPTX or DOCX.</p>
      ) : (
        <ul className="space-y-2">
          {docs.map((doc) => {
            const status = DOC_STATUS[doc.processingStatus] ?? DOC_STATUS.pending;
            return (
              <li key={doc.id} className="flex items-center gap-3 rounded-xl border border-zinc-200 px-3 py-2">
                <span className="shrink-0 rounded bg-zinc-100 px-1.5 py-0.5 text-xs font-semibold uppercase text-zinc-600">
                  {doc.fileType}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm text-zinc-700" title={doc.filename}>{doc.filename}</p>
                  <p className={`text-xs ${status.cls}`}>{status.label}</p>
                </div>
                <button
                  onClick={() => handleRemove(doc.id)}
                  className="shrink-0 rounded px-2 py-1 text-xs text-zinc-400 hover:bg-red-50 hover:text-red-500"
                >
                  Remove
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

// ─── Topics: pick one to start a learning session ────────────────────────────

function TopicsPanel({ courseId, onMockMode }: { courseId: string; onMockMode: (mock: boolean) => void }) {
  const router = useRouter();
  const [data, setData] = useState<TopicList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [startingId, setStartingId] = useState<number | null>(null);

  // State is only set inside the promise callbacks (never synchronously in the effect).
  const load = useCallback(
    () =>
      apiService
        .get<TopicList>(`/courses/${courseId}/topics`)
        .then((d) => {
          setData(d);
          setError("");
          onMockMode(d.mockMode);
        })
        .catch((e: Error) => setError(e.message))
        .finally(() => setLoading(false)),
    [courseId, onMockMode]
  );

  useEffect(() => {
    load();
  }, [load]);

  function retry() {
    setLoading(true);
    setError("");
    load();
  }

  async function start(topicId: number) {
    setStartingId(topicId);
    try {
      const session = await apiService.post<{ id: number }>(`/courses/${courseId}/topics/${topicId}/sessions`, {});
      router.push(`/courses/${courseId}/learn/${session.id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not start the session.");
      setStartingId(null);
    }
  }

  return (
    <section className="rounded-2xl border border-zinc-200 bg-white p-6">
      <h2 className="mb-1 text-xs font-semibold uppercase tracking-wider text-zinc-500">Topics</h2>
      <p className="mb-4 text-xs text-zinc-400">
        Extracted from your ingested materials. Pick one to start a lesson.
      </p>

      {loading && <p className="text-sm text-zinc-500">Finding topics in your materials…</p>}

      {error && (
        <div className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-600">
          <p>{error}</p>
          <button onClick={retry} className="mt-2 rounded-lg border border-red-300 px-3 py-1 text-xs font-medium hover:bg-red-100">
            Retry
          </button>
        </div>
      )}

      {!loading && !error && data && !data.hasContent && (
        <p className="text-sm text-zinc-500">
          No ingested material yet. Upload a file and wait until it shows “Ready”, then reload this page.
        </p>
      )}

      {!loading && !error && data && (
        <ul className="space-y-3">
          {data.topics.map((topic, i) => {
            const status = TOPIC_STATUS[topic.status] ?? TOPIC_STATUS.new;
            return (
              <li key={topic.id} className="rounded-xl border border-zinc-200 p-4">
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <h3 className="text-sm font-semibold text-zinc-900">
                      {i + 1}. {topic.title}
                    </h3>
                    {topic.summary && <p className="mt-1 text-sm leading-relaxed text-zinc-500">{topic.summary}</p>}
                    <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
                      <span className={`rounded-full px-2 py-0.5 font-medium ${status.cls}`}>{status.label}</span>
                      <span className="text-zinc-400">Difficulty level {topic.difficultyLevel} / 5</span>
                    </div>
                  </div>
                  <button
                    onClick={() => start(topic.id)}
                    disabled={startingId !== null}
                    className="shrink-0 rounded-lg bg-zinc-900 px-4 py-2 text-xs font-medium text-white hover:bg-zinc-700 disabled:opacity-50"
                  >
                    {startingId === topic.id ? "Starting…" : status.action}
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

// ─── Page ────────────────────────────────────────────────────────────────────

export default function LearnPage() {
  // useParams needs a Suspense boundary in this Next.js version.
  return (
    <Suspense>
      <LearnContent />
    </Suspense>
  );
}

function LearnContent() {
  const { id } = useParams<{ id: string }>();
  const [course, setCourse] = useState<Course | null>(null);
  const [mockMode, setMockMode] = useState(false);

  useEffect(() => {
    apiService.get<Course>(`/courses/${id}`).then(setCourse).catch(() => {});
  }, [id]);

  return (
    <div className="min-h-screen bg-zinc-50">
      <header className="border-b border-zinc-200 bg-white px-6 py-4">
        <div className="mx-auto flex max-w-6xl items-center gap-4">
          <Link href="/" className="text-sm text-zinc-500 hover:text-zinc-800">← Dashboard</Link>
          <span className="text-zinc-300">/</span>
          <h1 className="flex-1 truncate text-sm font-semibold text-zinc-900">
            {course ? course.title : "…"} · Start Learning
          </h1>
          <Link
            href={`/courses/${id}/history`}
            className="rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50"
          >
            History
          </Link>
          {mockMode && <MockBadge />}
        </div>
      </header>

      <main className="mx-auto grid max-w-6xl grid-cols-1 gap-6 px-6 py-8 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <TopicsPanel courseId={id} onMockMode={setMockMode} />
        </div>
        <div>
          <MaterialsPanel courseId={id} />
        </div>
      </main>
    </div>
  );
}
