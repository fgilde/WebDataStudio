import { readNdjson, type QueryChunk } from "../query/runQuery";

export interface FederationSource { connectionId: string; sql: string; alias: string }

export interface FederationRequest {
  sources: FederationSource[];
  sql: string;
  maxRowsPerSource?: number;
}

export interface FederationPlan { sources: { alias: string; ddl: string }[] }

/// What the run would stage, without copying anything.
export async function previewFederation(request: FederationRequest): Promise<FederationPlan> {
  const response = await fetch("/api/federate/preview", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify(request),
  });

  const text = await response.text();
  if (!response.ok) {
    let message = text;
    try { const body = JSON.parse(text); if (body?.message) message = body.message; } catch { /* not JSON */ }
    throw new Error(message);
  }

  return JSON.parse(text) as FederationPlan;
}

/// The same NDJSON the query endpoint speaks, so the result store and the grid need no idea that
/// several databases were involved.
export async function runFederation(
  request: FederationRequest, onChunk: (chunk: QueryChunk) => void): Promise<void> {
  let response: Response;
  try {
    response = await fetch("/api/federate/run", {
      method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify(request),
    });
  } catch (e) {
    onChunk({
      type: "error", statement: 0, text: e instanceof Error ? e.message : String(e),
      code: null, line: null, column: null,
    });
    return;
  }

  if (!response.ok || !response.body) {
    const text = await response.text();
    let message = text;
    try { const body = JSON.parse(text); if (body?.message) message = body.message; } catch { /* not JSON */ }
    onChunk({ type: "error", statement: 0, text: message, code: null, line: null, column: null });
    return;
  }

  await readNdjson(response.body, onChunk);
}
