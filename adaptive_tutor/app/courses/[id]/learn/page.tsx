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
type TopicList = {
  mockMode: boolean;
  hasContent: boolean;
  newTopics: number; // topics just added from newly ingested files
  testDue: boolean; // a mandatory topic test must be taken before another topic
  testTopics: string[]; // titles of the completed topics the test will cover
  topics: Topic[];
};

// What a file contains, checked before ingestion (no LLM call). warnings is
// empty when the file is the kind of material the tutor handles well.
type Scope = {
  unit: string; // "pages" or "slides"
  pages: number;
  pagesWithoutText: number;
  words: number;
  estimatedChunks: number;
  suggestedFirstPages: number;
  warnings: string[];
};
// How much of the file to ingest (the learner's answer to a scope warning).
type IngestChoice = { choice: "range" | "first" | "continue"; fromPage?: number; toPage?: number };
type Capabilities = { can: string[]; cannot: string[] };

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242";

const DOC_STATUS: Record<string, { label: string; cls: string }> = {
  pending: { label: "Not ingested yet", cls: "text-zinc-400" },
  processing: { label: "Processing…", cls: "text-blue-500" },
  completed: { label: "Ready", cls: "text-green-600" },
  failed: { label: "Ingestion failed", cls: "text-red-500" },
};

const TOPIC_STATUS: Record<Topic["status"], { label: string; cls: string; action: string }> = {
  new: { label: "Not started", cls: "bg-zinc-100 text-zinc-600", action: "Start" },
  learning: { label: "In progress", cls: "bg-blue-100 text-blue-700", action: "Continue" },
  completed: { label: "Completed", cls: "bg-green-100 text-green-700", action: "Learn again" },
  weak: { label: "Needs re-teaching", cls: "bg-amber-100 text-amber-800", action: "Re-learn" },
};

// ─── Scope popup: shown before ingesting a file the tutor may handle badly ───
// The server decides when to warn and writes the message; the learner decides
// what to do. Nothing is ingested until one of the choices is picked.

function ScopePopup({
  filename, scope, onChoose, onCancel,
}: {
  filename: string;
  scope: Scope;
  onChoose: (choice: IngestChoice) => void;
  onCancel: () => void;
}) {
  const [from, setFrom] = useState(1);
  const [to, setTo] = useState(scope.suggestedFirstPages);
  const [firstN, setFirstN] = useState(scope.suggestedFirstPages);
  const rangeOk = from >= 1 && to <= scope.pages && from <= to;
  const firstOk = firstN >= 1 && firstN <= scope.pages;
  const numberInput = "w-16 rounded border border-zinc-300 px-2 py-1 text-sm text-zinc-900";
  const choiceButton = "rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50";

  return (
    <div className="fixed inset-0 z-10 flex items-center justify-center bg-black/40 px-4" role="dialog" aria-modal="true" aria-labelledby="scope-title">
      <div className="w-full max-w-lg rounded-2xl bg-white p-6 shadow-xl">
        <h3 id="scope-title" className="text-base font-semibold text-zinc-900">Before we ingest this file</h3>
        <p className="mt-0.5 truncate text-xs text-zinc-500" title={filename}>{filename}</p>

        {scope.warnings.map((w, i) => (
          <p key={i} className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">{w}</p>
        ))}
        <p className="mt-3 text-xs text-zinc-500">
          {scope.pages} {scope.unit} · about {scope.words.toLocaleString()} words · about {scope.estimatedChunks} chunks
          {scope.pagesWithoutText > 0 && ` · ${scope.pagesWithoutText} ${scope.unit} with no text`}
        </p>

        <div className="mt-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2 text-sm text-zinc-700">
            <span>From</span>
            <input type="number" min={1} max={scope.pages} value={from} onChange={(e) => setFrom(Number(e.target.value))} className={numberInput} aria-label="First page of the range" />
            <span>to</span>
            <input type="number" min={1} max={scope.pages} value={to} onChange={(e) => setTo(Number(e.target.value))} className={numberInput} aria-label="Last page of the range" />
            <button onClick={() => onChoose({ choice: "range", fromPage: from, toPage: to })} disabled={!rangeOk} className={choiceButton}>
              Ingest a page range
            </button>
          </div>
          <div className="flex flex-wrap items-center gap-2 text-sm text-zinc-700">
            <span>First</span>
            <input type="number" min={1} max={scope.pages} value={firstN} onChange={(e) => setFirstN(Number(e.target.value))} className={numberInput} aria-label="Number of pages from the start" />
            <span>{scope.unit}</span>
            <button onClick={() => onChoose({ choice: "first", toPage: firstN })} disabled={!firstOk} className={choiceButton}>
              Ingest first {firstOk ? firstN : "N"} {scope.unit}
            </button>
          </div>
        </div>

        <div className="mt-5 flex justify-end gap-2 border-t border-zinc-100 pt-4">
          <button onClick={() => onChoose({ choice: "continue" })} className={choiceButton}>Continue anyway</button>
          <button onClick={onCancel} className="rounded-lg bg-zinc-900 px-4 py-1.5 text-xs font-medium text-white hover:bg-zinc-700">
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}

// ─── Permanent card: what the tutor can and cannot do ────────────────────────
// Always visible on the course page, so expectations are set before the
// learner starts, not only after something goes wrong.

function CapabilityCard() {
  const [caps, setCaps] = useState<Capabilities | null>(null);

  useEffect(() => {
    apiService.get<Capabilities>("/capabilities").then(setCaps).catch(() => {});
  }, []);

  if (!caps) return null;
  return (
    <section className="rounded-2xl border border-zinc-200 bg-white p-6">
      <h2 className="mb-3 text-xs font-semibold uppercase tracking-wider text-zinc-500">What this tutor can and can&apos;t do</h2>
      <p className="mb-1 text-xs font-semibold text-green-700">It can</p>
      <ul className="mb-3 list-disc space-y-1 pl-4 text-xs leading-relaxed text-zinc-600">
        {caps.can.map((line) => <li key={line}>{line}</li>)}
      </ul>
      <p className="mb-1 text-xs font-semibold text-amber-700">Its limits</p>
      <ul className="list-disc space-y-1 pl-4 text-xs leading-relaxed text-zinc-600">
        {caps.cannot.map((line) => <li key={line}>{line}</li>)}
      </ul>
    </section>
  );
}

// ─── Materials: the uploaded files the topics are extracted from ─────────────

function MaterialsPanel({ courseId, onIngested }: { courseId: string; onIngested: () => void }) {
  const [docs, setDocs] = useState<Document[]>([]);
  const [uploading, setUploading] = useState(false);
  const [ingestingId, setIngestingId] = useState<number | null>(null);
  const [error, setError] = useState("");
  // Set while the scope popup is open for a file.
  const [scopeCheck, setScopeCheck] = useState<{ doc: Document; scope: Scope } | null>(null);
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
      // The server says why a file was refused and what to do instead.
      if (!res.ok) setError(`${file.name}: ${(await res.text()) || "upload failed."}`);
    }
    await fetchDocs();
    setUploading(false);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  // Ingestion is started by hand (it uses the LLM). When it succeeds, the topic
  // list is reloaded so the new file's topics appear.
  async function ingest(docId: number, choice: IngestChoice | Record<string, never>) {
    setIngestingId(docId);
    setError("");
    try {
      await apiService.post(`/courses/${courseId}/documents/${docId}/ingest`, choice);
      onIngested();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Ingestion failed.");
    }
    await fetchDocs();
    setIngestingId(null);
  }

  // "Ingest" first asks the server what the file contains. A file with a
  // warning opens the popup; any other file is ingested straight away.
  async function handleIngest(doc: Document) {
    setIngestingId(doc.id);
    setError("");
    try {
      const scope = await apiService.get<Scope>(`/courses/${courseId}/documents/${doc.id}/scope`);
      if (scope.warnings.length > 0) {
        setScopeCheck({ doc, scope });
        setIngestingId(null);
        return;
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : "The file could not be checked.");
      setIngestingId(null);
      return;
    }
    await ingest(doc.id, {});
  }

  function chooseScope(choice: IngestChoice) {
    if (!scopeCheck) return;
    const docId = scopeCheck.doc.id;
    setScopeCheck(null);
    ingest(docId, choice); // the server records the warning together with this choice
  }

  function cancelScope() {
    if (!scopeCheck) return;
    // "Cancel" never reaches the ingest endpoint, so it is recorded separately.
    apiService
      .post(`/courses/${courseId}/events`, {
        eventType: "scope_warning",
        shownMessage: scopeCheck.scope.warnings.join(" "),
        userChoice: "Cancel",
      })
      .catch(() => {});
    setScopeCheck(null);
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
                  <p className={`text-xs ${status.cls}`}>
                    {ingestingId === doc.id ? "Ingesting… this can take a minute" : status.label}
                  </p>
                </div>
                {doc.processingStatus !== "completed" && (
                  <button
                    onClick={() => handleIngest(doc)}
                    disabled={ingestingId !== null}
                    className="shrink-0 rounded border border-zinc-300 px-2 py-1 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50"
                  >
                    {doc.processingStatus === "pending" ? "Ingest" : "Retry"}
                  </button>
                )}
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

      {scopeCheck && (
        <ScopePopup
          filename={scopeCheck.doc.filename}
          scope={scopeCheck.scope}
          onChoose={chooseScope}
          onCancel={cancelScope}
        />
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

  async function startTest() {
    setStartingId(-1);
    try {
      const session = await apiService.post<{ id: number }>(`/courses/${courseId}/topic-test`, {});
      router.push(`/courses/${courseId}/learn/${session.id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not start the test.");
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

      {!loading && !error && data && data.newTopics > 0 && (
        <p className="mb-3 rounded-lg border border-green-200 bg-green-50 px-3 py-2 text-xs text-green-800">
          {data.newTopics} new {data.newTopics === 1 ? "topic was" : "topics were"} added from newly ingested files (at the end of the list).
        </p>
      )}

      {error && (
        <div className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-600">
          <p>{error}</p>
          <button onClick={retry} className="mt-2 rounded-lg border border-red-300 px-3 py-1 text-xs font-medium hover:bg-red-100">
            Retry
          </button>
        </div>
      )}

      {/* Mandatory topic test: the topic buttons stay locked until it is taken. */}
      {!loading && !error && data && data.testDue && (
        <div className="mb-4 rounded-xl border border-purple-200 bg-purple-50 px-4 py-3">
          <p className="text-sm font-semibold text-purple-900">Mandatory topic test</p>
          <p className="mt-1 text-xs text-purple-800">
            You completed {data.testTopics.length} topics ({data.testTopics.join(", ")}). Take a short test on them
            before starting another topic. Topics scoring below 60% will be marked for re-teaching.
          </p>
          <button
            onClick={startTest}
            disabled={startingId !== null}
            className="mt-3 rounded-lg bg-purple-700 px-4 py-2 text-xs font-medium text-white hover:bg-purple-600 disabled:opacity-50"
          >
            {startingId === -1 ? "Starting…" : "Take the test"}
          </button>
        </div>
      )}

      {!loading && !error && data && !data.hasContent && (
        <p className="text-sm text-zinc-500">
          No ingested material yet. Add a file under Materials and click “Ingest”.
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
                    disabled={startingId !== null || data.testDue}
                    title={data.testDue ? "Take the mandatory topic test first." : undefined}
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
  // Increased after a file is ingested: remounts the topics panel so it reloads.
  const [topicsVersion, setTopicsVersion] = useState(0);

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
          <TopicsPanel key={topicsVersion} courseId={id} onMockMode={setMockMode} />
        </div>
        <div className="space-y-6">
          <MaterialsPanel courseId={id} onIngested={() => setTopicsVersion((v) => v + 1)} />
          <CapabilityCard />
        </div>
      </main>
    </div>
  );
}
