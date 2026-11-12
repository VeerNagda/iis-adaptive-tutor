"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { apiService } from "../../../services/apiService";

// ─── Types ───────────────────────────────────────────────────────────────────

type Course = { id: number; title: string; description: string | null; createdAt: string };
type Document = {
  id: number; filename: string; fileType: string; uploadedAt: string;
  processingStatus: string; errorMessage: string | null;
};
type Session = { id: number; status: string; startedAt: string; endedAt: string | null };

// ─── Helpers ─────────────────────────────────────────────────────────────────

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242";

const EXT_COLOR: Record<string, string> = {
  pdf: "bg-red-100 text-red-700",
  pptx: "bg-orange-100 text-orange-700",
  docx: "bg-blue-100 text-blue-700",
};

const INGEST_STATUS: Record<string, { label: string; cls: string; icon: "spin" | "check" | "clock" }> = {
  pending:    { label: "Ingestion pending", cls: "text-zinc-400", icon: "clock" },
  processing: { label: "Processing…",       cls: "text-blue-500", icon: "spin"  },
  completed:  { label: "Ready",             cls: "text-green-600", icon: "check" },
  failed:     { label: "Ingestion pending", cls: "text-zinc-400", icon: "clock" },
};

const STATUS_STYLE: Record<string, string> = {
  active: "bg-green-100 text-green-700",
  completed: "bg-zinc-100 text-zinc-600",
  paused: "bg-yellow-100 text-yellow-700",
};

function formatDate(raw: string) {
  const d = new Date(raw);
  if (isNaN(d.getTime())) return raw;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function formatTime(raw: string) {
  const d = new Date(raw);
  if (isNaN(d.getTime())) return raw;
  return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

// ─── Sub-components ──────────────────────────────────────────────────────────

function SectionHeader({ title, action }: { title: string; action?: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between mb-4">
      <h2 className="text-xs font-semibold uppercase tracking-wider text-zinc-500">{title}</h2>
      {action}
    </div>
  );
}

function Spinner() {
  return (
    <svg className="h-4 w-4 animate-spin text-zinc-400" fill="none" viewBox="0 0 24 24">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
    </svg>
  );
}

function IngestionBadge({ status }: { status: string }) {
  const cfg = INGEST_STATUS[status] ?? INGEST_STATUS.pending;
  return (
    <span className={`inline-flex items-center gap-1 text-xs font-medium ${cfg.cls}`}>
      {cfg.icon === "spin" && (
        <svg className="h-3 w-3 animate-spin" fill="none" viewBox="0 0 24 24">
          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
        </svg>
      )}
      {cfg.icon === "check" && (
        <svg className="h-3 w-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
        </svg>
      )}
      {cfg.icon === "clock" && (
        <svg className="h-3 w-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M12 6v6l4 2m6-2a10 10 0 11-20 0 10 10 0 0120 0z" />
        </svg>
      )}
      {cfg.label}
    </span>
  );
}

// ─── Materials panel ─────────────────────────────────────────────────────────

function MaterialsPanel({ courseId }: { courseId: string }) {
  const [docs, setDocs] = useState<Document[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [removingId, setRemovingId] = useState<number | null>(null);
  const [error, setError] = useState("");
  const fileInputRef = useRef<HTMLInputElement>(null);

  function fetchDocs() {
    return apiService
      .get<Document[]>(`/courses/${courseId}/documents`)
      .then(setDocs)
      .catch(() => setError("Failed to load materials."));
  }

  // Initial load
  useEffect(() => {
    fetchDocs().finally(() => setLoading(false));
  }, [courseId]); // eslint-disable-line react-hooks/exhaustive-deps

  // Poll every 5 s while any document is pending or processing
  useEffect(() => {
    const inFlight = docs.some(
      (d) => d.processingStatus === "pending" || d.processingStatus === "processing"
    );
    if (!inFlight) return;
    const id = setInterval(fetchDocs, 5000);
    return () => clearInterval(id);
  }, [docs]); // eslint-disable-line react-hooks/exhaustive-deps

  async function handleFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    setUploading(true);
    setError("");

    const results = await Promise.allSettled(
      Array.from(files).map((file) => {
        const form = new FormData();
        form.append("file", file);
        return fetch(`${BASE}/courses/${courseId}/documents`, { method: "POST", body: form })
          .then((res) => {
            if (!res.ok) throw new Error(file.name);
            return res.json() as Promise<Document>;
          });
      })
    );

    const added: Document[] = [];
    const failed: string[] = [];
    for (const r of results) {
      if (r.status === "fulfilled") added.push(r.value);
      else failed.push((r.reason as Error).message);
    }

    setDocs((prev) => [...added, ...prev]);
    if (failed.length) setError(`Failed to upload: ${failed.join(", ")}`);
    setUploading(false);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  async function handleRemove(docId: number) {
    setRemovingId(docId);
    try {
      await apiService.delete(`/courses/${courseId}/documents/${docId}`);
      setDocs((prev) => prev.filter((d) => d.id !== docId));
    } catch {
      setError("Failed to remove the file.");
    } finally {
      setRemovingId(null);
    }
  }

  return (
    <div className="flex flex-col h-full">
      <SectionHeader
        title="Materials"
        action={
          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={uploading}
            className="inline-flex items-center gap-1.5 rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 disabled:opacity-50 transition-colors"
          >
            {uploading ? <Spinner /> : (
              <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" />
              </svg>
            )}
            {uploading ? "Uploading…" : "Add files"}
          </button>
        }
      />

      <input
        ref={fileInputRef}
        type="file"
        multiple
        accept=".pdf,.pptx,.docx"
        className="hidden"
        onChange={(e) => handleFiles(e.target.files)}
      />

      {error && (
        <p className="mb-3 rounded-lg bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-600">{error}</p>
      )}

      {loading ? (
        <div className="flex justify-center py-8"><Spinner /></div>
      ) : docs.length === 0 ? (
        <div
          onClick={() => fileInputRef.current?.click()}
          className="flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed border-zinc-200 py-10 cursor-pointer hover:border-zinc-300 hover:bg-zinc-50 transition-colors"
        >
          <svg className="h-8 w-8 text-zinc-300" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 16v-8m0 0-3 3m3-3 3 3M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1" />
          </svg>
          <p className="text-xs text-zinc-400">Click to upload PDF, PPTX or DOCX</p>
        </div>
      ) : (
        <ul className="space-y-2 overflow-y-auto flex-1">
          {docs.map((doc) => (
            <li
              key={doc.id}
              className="flex items-center gap-3 rounded-xl border border-zinc-200 bg-white px-4 py-3"
            >
              <span className={`shrink-0 rounded px-1.5 py-0.5 text-xs font-semibold uppercase ${EXT_COLOR[doc.fileType] ?? "bg-zinc-100 text-zinc-600"}`}>
                {doc.fileType}
              </span>
              <div className="flex-1 min-w-0">
                <p className="truncate text-sm text-zinc-700" title={doc.filename}>{doc.filename}</p>
                <IngestionBadge status={doc.processingStatus} />
              </div>
              <span className="shrink-0 text-xs text-zinc-400">{formatDate(doc.uploadedAt)}</span>
              <button
                onClick={() => handleRemove(doc.id)}
                disabled={removingId === doc.id}
                className="shrink-0 rounded p-1 text-zinc-400 hover:text-red-500 hover:bg-red-50 disabled:opacity-40 transition-colors"
                title="Remove"
              >
                {removingId === doc.id ? <Spinner /> : (
                  <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                )}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// ─── Sessions panel ───────────────────────────────────────────────────────────

function SessionsPanel({ courseId }: { courseId: string }) {
  const router = useRouter();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    apiService
      .get<Session[]>(`/courses/${courseId}/sessions`)
      .then(setSessions)
      .catch(() => setError("Failed to load sessions."))
      .finally(() => setLoading(false));
  }, [courseId]);

  async function handleNewChat() {
    setCreating(true);
    try {
      const session = await apiService.post<Session>(`/courses/${courseId}/sessions`, {});
      router.push(`/courses/${courseId}/chat/${session.id}`);
    } catch {
      setError("Failed to create a new chat.");
      setCreating(false);
    }
  }

  return (
    <div className="flex flex-col h-full">
      <SectionHeader
        title="Chats"
        action={
          <button
            onClick={handleNewChat}
            disabled={creating}
            className="inline-flex items-center gap-1.5 rounded-lg bg-zinc-900 px-3 py-1.5 text-xs font-medium text-white hover:bg-zinc-700 disabled:opacity-50 transition-colors"
          >
            {creating ? <Spinner /> : (
              <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" />
              </svg>
            )}
            {creating ? "Starting…" : "New chat"}
          </button>
        }
      />

      {error && (
        <p className="mb-3 rounded-lg bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-600">{error}</p>
      )}

      {loading ? (
        <div className="flex justify-center py-8"><Spinner /></div>
      ) : sessions.length === 0 ? (
        <div className="flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed border-zinc-200 py-10 text-center">
          <svg className="h-8 w-8 text-zinc-300" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
          </svg>
          <p className="text-xs text-zinc-400">No chats yet — start a new one</p>
        </div>
      ) : (
        <ul className="space-y-2 overflow-y-auto flex-1">
          {sessions.map((s, i) => (
            <li key={s.id} className="flex items-center gap-3 rounded-xl border border-zinc-200 bg-white px-4 py-3">
              <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-zinc-100 text-xs font-semibold text-zinc-500">
                {sessions.length - i}
              </div>
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-zinc-800">Chat #{sessions.length - i}</p>
                <p className="text-xs text-zinc-400">{formatTime(s.startedAt)}</p>
              </div>
              <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium capitalize ${STATUS_STYLE[s.status] ?? "bg-zinc-100 text-zinc-600"}`}>
                {s.status}
              </span>
              <Link
                href={`/courses/${courseId}/chat/${s.id}`}
                className="shrink-0 rounded-lg border border-zinc-300 px-3 py-1.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 transition-colors"
              >
                Open
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export default function CoursePage() {
  const { id } = useParams<{ id: string }>();
  const [course, setCourse] = useState<Course | null>(null);

  useEffect(() => {
    apiService.get<Course>(`/courses/${id}`).then(setCourse).catch(() => {});
  }, [id]);

  return (
    <div className="min-h-screen bg-zinc-50 flex flex-col">
      {/* Header */}
      <header className="border-b border-zinc-200 bg-white px-6 py-4">
        <div className="mx-auto flex max-w-6xl items-center gap-4">
          <Link
            href="/"
            className="inline-flex items-center gap-1.5 text-sm text-zinc-500 hover:text-zinc-800 transition-colors"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
            </svg>
            Dashboard
          </Link>
          <span className="text-zinc-300">/</span>
          {course ? (
            <div>
              <h1 className="text-sm font-semibold text-zinc-900">{course.title}</h1>
              {course.description && (
                <p className="text-xs text-zinc-400 mt-0.5 max-w-md truncate">{course.description}</p>
              )}
            </div>
          ) : (
            <div className="h-4 w-40 rounded bg-zinc-200 animate-pulse" />
          )}
        </div>
      </header>

      {/* Two-panel body */}
      <div className="mx-auto w-full max-w-6xl flex-1 px-6 py-8 grid grid-cols-1 gap-6 lg:grid-cols-2">
        {/* Materials */}
        <div className="rounded-2xl border border-zinc-200 bg-zinc-50 p-6 flex flex-col min-h-[400px]">
          <MaterialsPanel courseId={id} />
        </div>

        {/* Sessions */}
        <div className="rounded-2xl border border-zinc-200 bg-zinc-50 p-6 flex flex-col min-h-[400px]">
          <SessionsPanel courseId={id} />
        </div>
      </div>
    </div>
  );
}
