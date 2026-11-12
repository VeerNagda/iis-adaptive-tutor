"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { apiService } from "./services/apiService";

type Course = {
  id: number;
  title: string;
  description: string | null;
  createdAt: string;
};

function formatDate(raw: string) {
  const d = new Date(raw);
  if (isNaN(d.getTime())) return raw;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function CourseCard({ course }: { course: Course }) {
  return (
    <div className="group flex flex-col justify-between rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm hover:shadow-md hover:border-zinc-300 transition-all">
      <div className="mb-5">
        <div className="mb-2 flex items-start justify-between gap-2">
          <h2 className="text-base font-semibold text-zinc-900 leading-snug line-clamp-2">
            {course.title}
          </h2>
        </div>
        {course.description ? (
          <p className="text-sm text-zinc-500 line-clamp-3 leading-relaxed">{course.description}</p>
        ) : (
          <p className="text-sm text-zinc-400 italic">No description</p>
        )}
      </div>

      <div className="flex items-center justify-between gap-3">
        <span className="text-xs text-zinc-400">{formatDate(course.createdAt)}</span>
        <Link
          href={`/courses/${course.id}/chat`}
          className="inline-flex items-center gap-1.5 rounded-lg bg-zinc-900 px-4 py-2 text-xs font-medium text-white hover:bg-zinc-700 transition-colors"
        >
          <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
          </svg>
          Open chat
        </Link>
      </div>
    </div>
  );
}

function SkeletonCard() {
  return (
    <div className="rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm animate-pulse">
      <div className="mb-4 h-4 w-3/4 rounded bg-zinc-200" />
      <div className="mb-2 h-3 w-full rounded bg-zinc-100" />
      <div className="mb-6 h-3 w-2/3 rounded bg-zinc-100" />
      <div className="flex justify-between items-center">
        <div className="h-3 w-20 rounded bg-zinc-100" />
        <div className="h-7 w-24 rounded-lg bg-zinc-200" />
      </div>
    </div>
  );
}

export default function DashboardPage() {
  const [courses, setCourses] = useState<Course[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    apiService
      .get<Course[]>("/courses")
      .then(setCourses)
      .catch(() => setError("Could not load courses. Make sure the server is running."))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="min-h-screen bg-zinc-50">
      {/* Top bar */}
      <header className="border-b border-zinc-200 bg-white px-6 py-4">
        <div className="mx-auto flex max-w-5xl items-center justify-between">
          <div className="flex items-center gap-2.5">
            <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-zinc-900">
              <svg className="h-4 w-4 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
              </svg>
            </div>
            <span className="text-sm font-semibold text-zinc-900">Adaptive Tutor</span>
          </div>
          <Link
            href="/courses/new"
            className="inline-flex items-center gap-1.5 rounded-lg bg-zinc-900 px-4 py-2 text-sm font-medium text-white hover:bg-zinc-700 transition-colors"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" />
            </svg>
            New course
          </Link>
        </div>
      </header>

      {/* Main */}
      <main className="mx-auto max-w-5xl px-6 py-10">
        {/* Heading */}
        <div className="mb-8">
          <h1 className="text-2xl font-semibold text-zinc-900">Your courses</h1>
          <p className="mt-1 text-sm text-zinc-500">
            {loading
              ? "Loading…"
              : error
              ? ""
              : courses.length === 0
              ? "No courses yet. Create your first one to get started."
              : `${courses.length} course${courses.length !== 1 ? "s" : ""}`}
          </p>
        </div>

        {/* Error */}
        {error && (
          <div className="mb-6 rounded-xl bg-red-50 border border-red-200 px-5 py-4 text-sm text-red-600">
            {error}
          </div>
        )}

        {/* Skeleton */}
        {loading && (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {[...Array(3)].map((_, i) => <SkeletonCard key={i} />)}
          </div>
        )}

        {/* Course grid */}
        {!loading && !error && courses.length > 0 && (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {courses.map((c) => <CourseCard key={c.id} course={c} />)}
          </div>
        )}

        {/* Empty state */}
        {!loading && !error && courses.length === 0 && (
          <div className="flex flex-col items-center justify-center rounded-2xl border-2 border-dashed border-zinc-300 bg-white py-20 text-center">
            <div className="mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-zinc-100">
              <svg className="h-7 w-7 text-zinc-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
              </svg>
            </div>
            <h2 className="text-base font-semibold text-zinc-700 mb-1">No courses yet</h2>
            <p className="text-sm text-zinc-400 mb-6 max-w-xs">
              Create your first course and upload study materials to get started.
            </p>
            <Link
              href="/courses/new"
              className="inline-flex items-center gap-1.5 rounded-lg bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white hover:bg-zinc-700 transition-colors"
            >
              <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 4v16m8-8H4" />
              </svg>
              Create a course
            </Link>
          </div>
        )}
      </main>
    </div>
  );
}
