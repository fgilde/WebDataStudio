const base = "/api";

/// One transaction a tab is holding open.
export interface OpenTransactionDto {
  id: string;
  connectionId: string;
  started: string;
  statements: number;
  lastStatement: string | null;
}

const ok = async <T>(response: Response): Promise<T> => {
  if (response.ok) return await response.json() as T;

  const text = await response.text();
  try {
    throw new Error((JSON.parse(text) as { message?: string }).message ?? text);
  } catch (e) {
    throw e instanceof Error ? e : new Error(text);
  }
};

const post = (path: string, body?: unknown) =>
  fetch(`${base}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body ?? {}),
  });

/// Opens a transaction and keeps it open until it is committed or rolled back. The statements a tab
/// runs afterwards go inside it, and nothing is written until somebody says so.
export const beginTransaction = (connectionId: string): Promise<OpenTransactionDto> =>
  post("/tx/begin", { connectionId }).then(r => ok<OpenTransactionDto>(r));

export const commitTransaction = (id: string): Promise<{ committed: boolean }> =>
  post(`/tx/${id}/commit`).then(r => ok<{ committed: boolean }>(r));

export const rollbackTransaction = (id: string): Promise<{ rolledBack: boolean }> =>
  post(`/tx/${id}/rollback`).then(r => ok<{ rolledBack: boolean }>(r));

/// What is open right now, and how long an untouched one lives before the server rolls it back.
export const openTransactions = (): Promise<{
  idleTimeoutSeconds: number; open: OpenTransactionDto[];
}> =>
  fetch(`${base}/tx`).then(r => ok<{ idleTimeoutSeconds: number; open: OpenTransactionDto[] }>(r));

/// How long a transaction has been open, in the words somebody would use.
export function heldFor(started: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(started).getTime()) / 1000));

  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;

  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}
