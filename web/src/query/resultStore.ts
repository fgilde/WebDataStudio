import type { QueryChunk, QueryColumn } from "./runQuery";

export interface QueryError { text: string; code: string | null; line: number | null; column: number | null }

export interface StatementResult {
  index: number;
  columns: QueryColumn[];
  rows: unknown[][];
  rowsAffected: number | null;
  elapsedMs: number | null;
  rowsRead: number;
  truncated: boolean;
  error: QueryError | null;
  running: boolean;
}

export interface ResultMessage { statement: number; severity: string; text: string }

export interface ResultState {
  statements: StatementResult[];
  messages: ResultMessage[];
  cancelled: boolean;
}

export const createResultState = (): ResultState => ({ statements: [], messages: [], cancelled: false });

const empty = (index: number): StatementResult => ({
  index, columns: [], rows: [], rowsAffected: null, elapsedMs: null,
  rowsRead: 0, truncated: false, error: null, running: true,
});

// Pure reducer: the panel keeps one ResultState in React state and replaces it per chunk.
export function applyChunk(state: ResultState, chunk: QueryChunk): ResultState {
  if (chunk.type === "cancelled") return { ...state, cancelled: true };

  const statements = state.statements.slice();
  while (statements.length <= chunk.statement) statements.push(empty(statements.length));
  const target = { ...statements[chunk.statement] };

  switch (chunk.type) {
    case "columns":
      target.columns = chunk.columns;
      break;
    case "rows":
      target.rows = target.rows.concat(chunk.rows);
      target.rowsRead = target.rows.length;
      break;
    case "progress":
      target.rowsRead = chunk.rowsRead;
      target.elapsedMs = chunk.elapsedMs;
      break;
    case "message":
      statements[chunk.statement] = target;
      return {
        ...state,
        statements,
        messages: state.messages.concat({ statement: chunk.statement, severity: chunk.severity, text: chunk.text }),
      };
    case "end":
      // ADO reports -1 for statements that return rows; the UI should show nothing, not "-1".
      target.rowsAffected = chunk.rowsAffected < 0 ? null : chunk.rowsAffected;
      target.elapsedMs = chunk.elapsedMs;
      target.truncated = chunk.truncated;
      target.running = false;
      break;
    case "error":
      target.error = { text: chunk.text, code: chunk.code, line: chunk.line, column: chunk.column };
      target.running = false;
      break;
  }

  statements[chunk.statement] = target;
  return { ...state, statements };
}
