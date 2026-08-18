const base = "/api";

export interface Me { anonymous: boolean; authenticated: boolean; username: string | null }
export interface Connection {
  id: string; name: string; engine: string; readOnly: boolean;
  color: string | null; group: string | null; source: "Environment" | "Stored"; summary: string;
  tunnelled: boolean;
}
export interface TunnelInput {
  host: string; port: number; user: string;
  password?: string | null; privateKey?: string | null; passphrase?: string | null;
}

export interface ConnectionInput {
  name: string; engine: string; connectionString: string;
  readOnly: boolean; color?: string | null; group?: string | null;
  /// Omitted on an edit keeps the stored tunnel: the private key never travels back to the browser.
  tunnel?: TunnelInput | null;
}

let onUnauthorized: () => void = () => {};
export const setOnUnauthorized = (fn: () => void) => { onUnauthorized = fn; };

// The API answers errors as { message }; show that, not the raw body with a status glued on.
async function fail(r: Response): Promise<never> {
  const text = await r.text();
  let message = text;
  try { const j = JSON.parse(text); if (typeof j?.message === "string" && j.message) message = j.message; } catch { /* not JSON */ }
  throw new Error(message.trim() || `${r.status} ${r.statusText}`.trim());
}

async function ok<T>(r: Response): Promise<T> {
  if (r.status === 401) onUnauthorized();
  if (!r.ok) await fail(r);
  return r.status === 204 ? (undefined as T) : r.json();
}

const json = (method: string, body: unknown) => ({
  method, headers: { "content-type": "application/json" }, body: JSON.stringify(body),
});

export const me = (): Promise<Me> => fetch(`${base}/auth/me`).then(r => ok<Me>(r));

// Login must not trigger the unauthorized handler: a wrong password is an expected answer here.
export const login = (username: string, password: string): Promise<Me> =>
  fetch(`${base}/auth/login`, json("POST", { username, password })).then(async r => {
    if (!r.ok) return fail(r);
    return r.json();
  });

export const logout = (): Promise<void> => fetch(`${base}/auth/logout`, { method: "POST" }).then(() => undefined);

export const listConnections = (): Promise<Connection[]> =>
  fetch(`${base}/connections`).then(r => ok<Connection[]>(r));
export const createConnection = (body: ConnectionInput): Promise<Connection> =>
  fetch(`${base}/connections`, json("POST", body)).then(r => ok<Connection>(r));
export const updateConnection = (id: string, body: ConnectionInput): Promise<Connection> =>
  fetch(`${base}/connections/${id}`, json("PUT", body)).then(r => ok<Connection>(r));
export const deleteConnection = (id: string): Promise<void> =>
  fetch(`${base}/connections/${id}`, { method: "DELETE" }).then(r => ok<void>(r));
export const testConnection = (body: ConnectionInput): Promise<{ ok: boolean; message: string }> =>
  fetch(`${base}/connections/test`, json("POST", body)).then(r => ok<{ ok: boolean; message: string }>(r));

export interface SchemaNodeDto {
  ref: string; kind: string; label: string; hasChildren: boolean; detail: string | null;
}
export interface ColumnDto {
  name: string; dataType: string; nullable: boolean; default: string | null;
  isPrimaryKey: boolean; isIdentity: boolean; comment: string | null; position: number;
}
export interface IndexDto { name: string; columns: string[]; unique: boolean; primary: boolean; filter: string | null }
export interface ForeignKeyDto {
  name: string; columns: string[]; referencedSchema: string; referencedTable: string;
  referencedColumns: string[]; onDelete: string; onUpdate: string;
}
export interface TriggerDto { name: string; timing: string; event: string }
export interface ObjectDetailDto {
  columns: ColumnDto[]; indexes: IndexDto[]; foreignKeys: ForeignKeyDto[]; triggers: TriggerDto[];
  rowCount: number | null; sizeBytes: number | null; comment: string | null; ddl: string | null;
}
export interface DriverDto {
  info: { id: string; label: string; defaultPort: number; connectionStringTemplate: string };
  caps: Record<string, boolean>;
}

export const listDrivers = (): Promise<DriverDto[]> => fetch(`${base}/drivers`).then(r => ok<DriverDto[]>(r));

export const listSchema = (conn: string, parent?: string): Promise<SchemaNodeDto[]> =>
  fetch(parent ? `${base}/schema/${conn}?parent=${encodeURIComponent(parent)}` : `${base}/schema/${conn}`)
    .then(r => ok<SchemaNodeDto[]>(r));

export const describeObject = (conn: string, ref: string): Promise<ObjectDetailDto> =>
  fetch(`${base}/schema/${conn}/object/${encodeURIComponent(ref)}`).then(r => ok<ObjectDetailDto>(r));

export interface HistoryEntryDto {
  id: number; connectionId: string; sql: string; executedAt: string;
  elapsedMs: number | null; rowCount: number | null; error: string | null;
}
export interface HistoryInput {
  connectionId: string; sql: string;
  elapsedMs: number | null; rowCount: number | null; error: string | null;
}

export const listHistory = (params: { connectionId?: string; search?: string; limit?: number } = {}):
  Promise<HistoryEntryDto[]> => {
  const query = new URLSearchParams();
  if (params.connectionId) query.set("connectionId", params.connectionId);
  if (params.search) query.set("search", params.search);
  if (params.limit) query.set("limit", String(params.limit));
  const suffix = query.toString();
  return fetch(`${base}/history${suffix ? `?${suffix}` : ""}`).then(r => ok<HistoryEntryDto[]>(r));
};

export const addHistory = (body: HistoryInput): Promise<void> =>
  fetch(`${base}/history`, json("POST", body)).then(r => ok<void>(r));

export const loadTabs = (): Promise<unknown[]> => fetch(`${base}/workspace/tabs`).then(r => ok<unknown[]>(r));

export const saveTabs = (tabs: unknown): Promise<void> =>
  fetch(`${base}/workspace/tabs`, {
    method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(tabs),
  }).then(r => ok<void>(r));

export interface ExportFormatDto {
  format: string; label: string; extension: string; contentType: string; supportsSchemaScope: boolean;
}

export const listExportFormats = (): Promise<ExportFormatDto[]> =>
  fetch(`${base}/export/formats`).then(r => ok<ExportFormatDto[]>(r));

export interface DataPageDto {
  columns: { name: string; dataType: string; nullable: boolean }[];
  rows: unknown[][];
  editable: boolean;
  keyColumns: string[];
  reason: string | null;
  totalEstimate: number | null;
  offset: number;
  limit: number;
}
export interface ChangePreviewDto {
  hash: string; script: string; statementCount: number; destructive: boolean;
}
export interface LookupItemDto { value: unknown; label: unknown }

export const browseData = (conn: string, ref: string,
  params: { offset?: number; limit?: number; sort?: string; desc?: boolean;
            filterColumn?: string; filter?: string } = {}): Promise<DataPageDto> => {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params))
    if (value !== undefined && value !== "") query.set(key, String(value));
  const suffix = query.toString();
  return fetch(`${base}/data/${conn}/${encodeURIComponent(ref)}${suffix ? `?${suffix}` : ""}`)
    .then(r => ok<DataPageDto>(r));
};

export const previewChanges = (conn: string, ref: string, changes: unknown[]): Promise<ChangePreviewDto> =>
  fetch(`${base}/data/${conn}/${encodeURIComponent(ref)}/preview-changes`, json("POST", { changes }))
    .then(r => ok<ChangePreviewDto>(r));

export const applyChanges = (conn: string, ref: string, hash: string): Promise<void> =>
  fetch(`${base}/data/${conn}/${encodeURIComponent(ref)}/apply-changes`, json("POST", { hash }))
    .then(r => ok<void>(r));

export const lookupValues = (conn: string, ref: string, column: string, search?: string):
  Promise<LookupItemDto[]> => {
  const query = new URLSearchParams({ column });
  if (search) query.set("search", search);
  return fetch(`${base}/data/${conn}/${encodeURIComponent(ref)}/lookup?${query}`)
    .then(r => ok<LookupItemDto[]>(r));
};

export interface PlanNodeDto {
  operation: string; detail: string | null;
  estimatedCost: number | null; estimatedRows: number | null;
  actualRows: number | null; actualMs: number | null;
  children: PlanNodeDto[]; warnings: string[];
}
export interface FindingDto {
  category: string; severity: string; title: string; detail: string; statement: string | null;
}
export interface AnalyzeResultDto {
  plan: PlanNodeDto | null;
  summary: { totalCost: number | null; maxNodeCost: number; nodeCount: number } | null;
  planError: string | null;
  findings: FindingDto[];
}
export interface ServerStatsDto {
  metrics: { name: string; value: string; detail: string | null }[];
  blocking: { sessionId: string; blockedBy: string; query: string; waitMs: number }[];
}

export const analyzeQuery = (connectionId: string, sql: string, actual = false): Promise<AnalyzeResultDto> =>
  fetch(`${base}/query/analyze`, json("POST", { connectionId, sql, actual }))
    .then(r => ok<AnalyzeResultDto>(r));

export const healthReport = (connectionId: string, schema?: string): Promise<{ findings: FindingDto[] }> =>
  fetch(`${base}/analyze/${connectionId}${schema ? `?schema=${encodeURIComponent(schema)}` : ""}`)
    .then(r => ok<{ findings: FindingDto[] }>(r));

export const serverStats = (connectionId: string): Promise<ServerStatsDto> =>
  fetch(`${base}/stats/${connectionId}`).then(r => ok<ServerStatsDto>(r));

import type { TableDefinition } from "./designer/definition";

export interface DdlStatementDto { sql: string; destructive: boolean; description: string }
export interface DdlPreviewDto {
  hash: string; statements: DdlStatementDto[]; script: string;
  destructive: boolean; transactional: boolean;
}
export interface DdlLoadDto { definition: TableDefinition; create: string | null; supported: boolean }
export interface DependencyReportDto { dependsOn: string[]; usedBy: string[]; bestEffort: boolean }

export const loadDdl = (conn: string, ref: string): Promise<DdlLoadDto> =>
  fetch(`${base}/ddl/${conn}/${encodeURIComponent(ref)}`).then(r => ok<DdlLoadDto>(r));

export const previewDdl = (conn: string, ref: string | null, after: TableDefinition): Promise<DdlPreviewDto> =>
  fetch(`${base}/ddl/${conn}/preview`, json("POST", { objectRef: ref, after }))
    .then(r => ok<DdlPreviewDto>(r));

export const applyDdl = (conn: string, hash: string): Promise<void> =>
  fetch(`${base}/ddl/${conn}/apply`, json("POST", { hash })).then(r => ok<void>(r));

export const dependencies = (conn: string, ref: string): Promise<DependencyReportDto> =>
  fetch(`${base}/ddl/${conn}/${encodeURIComponent(ref)}/dependencies`)
    .then(r => ok<DependencyReportDto>(r));

export const previewRename = (conn: string, ref: string, newName: string):
  Promise<{ hash: string; script: string; dependencies: DependencyReportDto }> =>
  fetch(`${base}/ddl/${conn}/rename`, json("POST", { objectRef: ref, newName }))
    .then(r => ok<{ hash: string; script: string; dependencies: DependencyReportDto }>(r));

// --- ER diagram --------------------------------------------------------------
export interface DiagramColumnDto {
  name: string; type: string; nullable: boolean; primaryKey: boolean; foreignKey: boolean;
}
export interface DiagramNodeDto {
  id: string; schema: string; name: string; columns: DiagramColumnDto[];
}
export interface DiagramEdgeDto {
  name: string; source: string; target: string;
  sourceColumns: string[]; targetColumns: string[]; resolved: boolean;
}

export const loadDiagram = (conn: string, schema?: string, refresh = false):
  Promise<{ nodes: DiagramNodeDto[]; edges: DiagramEdgeDto[] }> => {
  const query = new URLSearchParams();
  if (schema) query.set("schema", schema);
  if (refresh) query.set("refresh", "true");

  return fetch(`${base}/diagram/${conn}${query.size ? `?${query}` : ""}`)
    .then(r => ok<{ nodes: DiagramNodeDto[]; edges: DiagramEdgeDto[] }>(r));
};

// --- administration ----------------------------------------------------------
export interface SystemCommandDto {
  id: string; label: string; sql: string; needsTarget: boolean;
  destructive: boolean; description: string;
}
export interface SessionDto {
  id: string; user: string; database: string; query: string;
  state: string; durationMs: number; blockedBy: string | null;
}
export interface DatabaseDto { name: string; sizeBytes: number | null }
export interface ServerLogDto { available: boolean; reason: string | null; lines: string[] }

export const systemCommands = (conn: string): Promise<SystemCommandDto[]> =>
  fetch(`${base}/admin/system-commands/${conn}`).then(r => ok<SystemCommandDto[]>(r));

export const runSystemCommand = (conn: string, commandId: string, target?: string):
  Promise<{ executed: string }> =>
  fetch(`${base}/admin/system-command/${conn}`, json("POST", { commandId, target }))
    .then(r => ok<{ executed: string }>(r));

export const listSessions = (conn: string): Promise<SessionDto[]> =>
  fetch(`${base}/admin/sessions/${conn}`).then(r => ok<SessionDto[]>(r));

export const killSession = (conn: string, id: string): Promise<void> =>
  fetch(`${base}/admin/sessions/${conn}/${encodeURIComponent(id)}/kill`, { method: "POST" })
    .then(r => ok<void>(r));

export const listDatabases = (conn: string): Promise<DatabaseDto[]> =>
  fetch(`${base}/admin/databases/${conn}`).then(r => ok<DatabaseDto[]>(r));

export const createDatabase = (conn: string, name: string): Promise<void> =>
  fetch(`${base}/admin/databases/${conn}`, json("POST", { name })).then(r => ok<void>(r));

export const dropDatabase = (conn: string, name: string): Promise<void> =>
  fetch(`${base}/admin/databases/${conn}/${encodeURIComponent(name)}`, { method: "DELETE" })
    .then(r => ok<void>(r));

export const listUsers = (conn: string): Promise<string[]> =>
  fetch(`${base}/admin/users/${conn}`).then(r => ok<string[]>(r));

export const previewUserChange = (conn: string, body: {
  user: string; password?: string; privilege?: string; target?: string;
}): Promise<{ hash: string; script: string }> =>
  fetch(`${base}/admin/users/${conn}/preview`, json("POST", body))
    .then(r => ok<{ hash: string; script: string }>(r));

export const applyUserChange = (conn: string, hash: string): Promise<{ executed: string }> =>
  fetch(`${base}/admin/users/${conn}/apply`, json("POST", { hash }))
    .then(r => ok<{ executed: string }>(r));

export const serverLog = (conn: string, lines = 200): Promise<ServerLogDto> =>
  fetch(`${base}/admin/logs/${conn}?lines=${lines}`).then(r => ok<ServerLogDto>(r));

/// The backup answers with the dump itself, so it is downloaded rather than parsed.
export const downloadBackup = async (conn: string, body: {
  schemaOnly?: boolean; dataOnly?: boolean; tables?: string[];
}): Promise<void> => {
  const response = await fetch(`${base}/admin/backup/${conn}`, json("POST", body));
  if (!response.ok) await fail(response);

  const blob = await response.blob();
  const disposition = response.headers.get("content-disposition") ?? "";
  const name = /filename="([^"]+)"/.exec(disposition)?.[1] ?? "backup";

  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
};

export const restoreBackup = async (conn: string, file: File, confirm: string): Promise<string> => {
  const form = new FormData();
  form.append("file", file);
  form.append("confirm", confirm);

  const response = await fetch(`${base}/admin/restore/${conn}`, { method: "POST", body: form });
  if (!response.ok) await fail(response);
  return (await response.json()).message as string;
};

// --- compare -------------------------------------------------------------------
export interface SchemaComparisonDto {
  tablesOnlyInSource: string[]; tablesOnlyInTarget: string[];
  changedTables: {
    name: string; addedColumns: string[]; removedColumns: string[]; changedColumns: string[];
  }[];
  identicalTables: string[];
  script: string;
}
export interface DataComparisonDto {
  columns: string[]; keyColumns: string[];
  missing: unknown[][]; extra: unknown[][];
  different: { key: unknown[]; changedColumns: string[]; sourceRow: unknown[]; targetRow: unknown[] }[];
  identical: number; truncated: boolean; script: string;
}

export const compareSchemas = (body: {
  sourceConnectionId: string; sourceSchema?: string;
  targetConnectionId: string; targetSchema?: string;
}): Promise<SchemaComparisonDto> =>
  fetch(`${base}/compare/schema`, json("POST", body)).then(r => ok<SchemaComparisonDto>(r));

export const compareData = (body: {
  sourceConnectionId: string; sourceRef: string;
  targetConnectionId: string; targetRef: string;
  keyColumns: string[]; maxRows?: number;
}): Promise<DataComparisonDto> =>
  fetch(`${base}/compare/data`, json("POST", body)).then(r => ok<DataComparisonDto>(r));

// --- workspace items (snippets, layout presets) --------------------------------
export const loadWorkspaceItem = <T>(key: string): Promise<T | null> =>
  fetch(`${base}/workspace/item/${encodeURIComponent(key)}`).then(r => ok<T | null>(r));

export const saveWorkspaceItem = (key: string, value: unknown): Promise<void> =>
  fetch(`${base}/workspace/item/${encodeURIComponent(key)}`, {
    method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(value),
  }).then(r => ok<void>(r));

// --- saved queries ---------------------------------------------------------------
export interface SavedQueryDto {
  id: string; name: string; folder: string | null; sql: string;
  connectionId: string | null; updatedAt: string;
}

export const listSavedQueries = (): Promise<SavedQueryDto[]> =>
  fetch(`${base}/saved-queries`).then(r => ok<SavedQueryDto[]>(r));

export const createSavedQuery = (body: {
  name: string; folder?: string | null; sql: string; connectionId?: string | null;
}): Promise<SavedQueryDto> =>
  fetch(`${base}/saved-queries`, json("POST", body)).then(r => ok<SavedQueryDto>(r));

export const updateSavedQuery = (id: string, body: {
  name: string; folder?: string | null; sql: string; connectionId?: string | null;
}): Promise<SavedQueryDto> =>
  fetch(`${base}/saved-queries/${id}`, json("PUT", body)).then(r => ok<SavedQueryDto>(r));

export const deleteSavedQuery = (id: string): Promise<void> =>
  fetch(`${base}/saved-queries/${id}`, { method: "DELETE" }).then(r => ok<void>(r));

export interface SlowQueryDto { query: string; calls: number; totalMs: number; meanMs: number }

export const slowQueries = (conn: string): Promise<SlowQueryDto[]> =>
  fetch(`${base}/stats/${conn}/slow-queries`).then(r => ok<SlowQueryDto[]>(r));
