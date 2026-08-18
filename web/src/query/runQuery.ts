export interface QueryColumn { name: string; dataType: string; nullable: boolean }

export type QueryChunk =
  | { type: "columns"; statement: number; columns: QueryColumn[] }
  | { type: "rows"; statement: number; rows: unknown[][] }
  | { type: "documents"; statement: number; documents: unknown[] }
  | { type: "progress"; statement: number; rowsRead: number; elapsedMs: number }
  | { type: "message"; statement: number; severity: string; text: string }
  | { type: "end"; statement: number; rowsAffected: number; elapsedMs: number; truncated: boolean }
  | { type: "error"; statement: number; text: string; code: string | null; line: number | null; column: number | null }
  | { type: "cancelled" };

export interface QueryRequest {
  connectionId: string; sql: string;
  maxRows?: number; timeoutSeconds?: number; schema?: string;
  // Named bind variables; the statement itself keeps its :name / @name markers.
  parameters?: Record<string, string | null>;
  /// Wraps the whole script in one transaction: commit at the end, rollback on the first error.
  transactional?: boolean;
}

export interface QueryRun {
  runId: Promise<string | null>;
  done: Promise<void>;
  cancel: () => Promise<void>;
}

// Streams NDJSON from /api/query/execute, calling onChunk as each line arrives so the grid can
// render while the query is still running.
export function runQuery(request: QueryRequest, onChunk: (chunk: QueryChunk) => void): QueryRun {
  let resolveRunId: (id: string | null) => void = () => {};
  const runId = new Promise<string | null>(resolve => { resolveRunId = resolve; });
  let currentRunId: string | null = null;

  const done = (async () => {
    let response: Response;
    try {
      response = await fetch("/api/query/execute", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(request),
      });
    } catch (e) {
      resolveRunId(null);
      onChunk(errorChunk(e instanceof Error ? e.message : String(e)));
      return;
    }

    currentRunId = response.headers.get("X-Run-Id");
    resolveRunId(currentRunId);

    if (!response.ok || !response.body) {
      const text = await response.text();
      let message = text;
      try { const j = JSON.parse(text); if (j?.message) message = j.message; } catch { /* not JSON */ }
      onChunk(errorChunk(message));
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { done: finished, value } = await reader.read();
      if (finished) break;
      buffer += decoder.decode(value, { stream: true });

      let newline: number;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) emit(line, onChunk);
      }
    }
    if (buffer.trim()) emit(buffer.trim(), onChunk);
  })();

  return {
    runId,
    done,
    cancel: async () => {
      const id = currentRunId ?? (await runId);
      if (id) await fetch(`/api/query/${id}/cancel`, { method: "POST" });
    },
  };
}

const errorChunk = (text: string): QueryChunk =>
  ({ type: "error", statement: 0, text, code: null, line: null, column: null });

function emit(line: string, onChunk: (chunk: QueryChunk) => void) {
  // A malformed line must not kill a run that is otherwise fine.
  try { onChunk(JSON.parse(line) as QueryChunk); } catch { /* ignore */ }
}
