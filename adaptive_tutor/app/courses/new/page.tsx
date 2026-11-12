"use client";

import Link from "next/link";
import { useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { apiService } from "../../services/apiService";

type UploadedFile = { id: string; file: File };
type SubmitState = "idle" | "submitting" | "error";

const ACCEPTED_TYPES = [
  "application/pdf",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
];
const ACCEPTED_EXTENSIONS = ".pdf,.pptx,.docx";
const EXT_LABEL: Record<string, string> = {
  "application/pdf": "PDF",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation": "PPTX",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "DOCX",
};
const EXT_COLOR: Record<string, string> = {
  PDF: "bg-red-100 text-red-700",
  PPTX: "bg-orange-100 text-orange-700",
  DOCX: "bg-blue-100 text-blue-700",
};

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export default function CreateCoursePage() {
  const router = useRouter();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [files, setFiles] = useState<UploadedFile[]>([]);
  const [dragging, setDragging] = useState(false);
  const [state, setState] = useState<SubmitState>("idle");
  const [errorMsg, setErrorMsg] = useState("");
  const fileInputRef = useRef<HTMLInputElement>(null);

  function addFiles(incoming: FileList | null) {
    if (!incoming) return;
    const valid = Array.from(incoming).filter((f) => ACCEPTED_TYPES.includes(f.type));
    if (valid.length < incoming.length)
      setErrorMsg("Some files were skipped — only PDF, PPTX, and DOCX are accepted.");
    setFiles((prev) => [
      ...prev,
      ...valid
        .filter((f) => !prev.some((p) => p.file.name === f.name && p.file.size === f.size))
        .map((f) => ({ id: crypto.randomUUID(), file: f })),
    ]);
  }

  function removeFile(id: string) {
    setFiles((prev) => prev.filter((f) => f.id !== id));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!title.trim()) return;
    setState("submitting");
    setErrorMsg("");

    try {
      const course = await apiService.post<{ id: number }>("/courses", {
        title: title.trim(),
        description: description.trim() || null,
      });

      await Promise.all(
        files.map((f) => {
          const form = new FormData();
          form.append("file", f.file);
          return fetch(
            `${process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242"}/courses/${course.id}/documents`,
            { method: "POST", body: form }
          ).then((res) => {
            if (!res.ok) throw new Error(`Failed to upload ${f.file.name}`);
          });
        })
      );

      router.push("/");
    } catch (err) {
      setErrorMsg(err instanceof Error ? err.message : "Something went wrong.");
      setState("error");
    }
  }

  return (
    <div className="min-h-screen bg-zinc-50 px-4 py-10">
      <div className="mx-auto w-full max-w-2xl">
        {/* Back */}
        <Link
          href="/"
          className="mb-6 inline-flex items-center gap-1.5 text-sm text-zinc-500 hover:text-zinc-800 transition-colors"
        >
          <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to dashboard
        </Link>

        <div className="mb-8">
          <h1 className="text-2xl font-semibold text-zinc-900">Create a new course</h1>
          <p className="mt-1 text-sm text-zinc-500">
            Add course details and upload the relevant study materials.
          </p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-6">
          {/* Course details */}
          <div className="bg-white rounded-2xl border border-zinc-200 shadow-sm p-6 space-y-4">
            <h2 className="text-xs font-semibold text-zinc-500 uppercase tracking-wider">Course details</h2>

            <div>
              <label htmlFor="title" className="block text-sm font-medium text-zinc-700 mb-1">
                Title <span className="text-red-500">*</span>
              </label>
              <input
                id="title"
                type="text"
                required
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="e.g. Introduction to Machine Learning"
                className="w-full rounded-lg border border-zinc-300 px-3 py-2 text-sm text-zinc-900 placeholder-zinc-400 focus:outline-none focus:ring-2 focus:ring-zinc-900 focus:border-transparent transition"
              />
            </div>

            <div>
              <label htmlFor="description" className="block text-sm font-medium text-zinc-700 mb-1">
                Description
              </label>
              <textarea
                id="description"
                rows={3}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="What is this course about?"
                className="w-full rounded-lg border border-zinc-300 px-3 py-2 text-sm text-zinc-900 placeholder-zinc-400 focus:outline-none focus:ring-2 focus:ring-zinc-900 focus:border-transparent transition resize-none"
              />
            </div>
          </div>

          {/* File upload */}
          <div className="bg-white rounded-2xl border border-zinc-200 shadow-sm p-6 space-y-4">
            <h2 className="text-xs font-semibold text-zinc-500 uppercase tracking-wider">Study materials</h2>

            <div
              onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
              onDragLeave={() => setDragging(false)}
              onDrop={(e) => { e.preventDefault(); setDragging(false); addFiles(e.dataTransfer.files); }}
              onClick={() => fileInputRef.current?.click()}
              className={`flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed px-6 py-10 cursor-pointer transition-colors ${
                dragging ? "border-zinc-900 bg-zinc-50" : "border-zinc-300 bg-zinc-50 hover:border-zinc-400 hover:bg-zinc-100"
              }`}
            >
              <div className="flex h-10 w-10 items-center justify-center rounded-full bg-zinc-200">
                <svg className="h-5 w-5 text-zinc-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M12 16v-8m0 0-3 3m3-3 3 3M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1" />
                </svg>
              </div>
              <p className="text-sm text-zinc-600 font-medium">
                Drag &amp; drop files here, or <span className="text-zinc-900 underline">browse</span>
              </p>
              <p className="text-xs text-zinc-400">PDF, PPTX, DOCX</p>
              <input
                ref={fileInputRef}
                type="file"
                multiple
                accept={ACCEPTED_EXTENSIONS}
                className="hidden"
                onChange={(e) => addFiles(e.target.files)}
              />
            </div>

            {files.length > 0 && (
              <ul className="space-y-2">
                {files.map(({ id, file }) => {
                  const label = EXT_LABEL[file.type] ?? "FILE";
                  return (
                    <li key={id} className="flex items-center gap-3 rounded-lg border border-zinc-200 px-4 py-3 bg-zinc-50">
                      <span className={`shrink-0 rounded px-1.5 py-0.5 text-xs font-semibold ${EXT_COLOR[label] ?? "bg-zinc-100 text-zinc-600"}`}>
                        {label}
                      </span>
                      <span className="flex-1 truncate text-sm text-zinc-700">{file.name}</span>
                      <span className="shrink-0 text-xs text-zinc-400">{formatBytes(file.size)}</span>
                      <button
                        type="button"
                        onClick={() => removeFile(id)}
                        className="shrink-0 rounded p-1 text-zinc-400 hover:text-red-500 hover:bg-red-50 transition-colors"
                      >
                        <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {errorMsg && (
            <p className="rounded-lg bg-red-50 border border-red-200 px-4 py-3 text-sm text-red-600">
              {errorMsg}
            </p>
          )}

          <div className="flex justify-end gap-3">
            <Link
              href="/"
              className="rounded-lg border border-zinc-300 px-5 py-2.5 text-sm font-medium text-zinc-700 hover:bg-zinc-100 transition-colors"
            >
              Cancel
            </Link>
            <button
              type="submit"
              disabled={state === "submitting" || !title.trim()}
              className="flex items-center gap-2 rounded-lg bg-zinc-900 px-6 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {state === "submitting" && (
                <svg className="h-4 w-4 animate-spin" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
                </svg>
              )}
              {state === "submitting" ? "Creating…" : "Create course"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
