const base = "/api";

export interface Me {
  anonymous: boolean; authenticated: boolean; username: string | null;
  /// Name of this studio, from WDS_TITLE. Null when nothing named it.
  title?: string | null;
}
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

export interface HealthDto {
  status: string;
  version: string;
  commit: string | null;
  built: string;
  store: { path: string; available: boolean; error: string | null };
  connections: number;
}

export const health = (): Promise<HealthDto> => fetch(`${base}/health`).then(r => ok<HealthDto>(r));

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

/// An object reference contains a slash ("Table:dbo/AbpUsers"), so it travels as a query value
/// rather than a path segment: a reverse proxy — Envoy in front of Azure Container Apps, among
/// others — turns %2F back into a real slash before routing, and the request then matches no route
/// at all. Everything that addresses an object goes through refQuery for that reason.
const refQuery = (ref: string, extra?: URLSearchParams) => {
  const query = new URLSearchParams({ ref });
  extra?.forEach((value, key) => query.set(key, value));
  return query.toString();
};

export const describeObject = (conn: string, ref: string): Promise<ObjectDetailDto> =>
  fetch(`${base}/schema/${conn}/object?${refQuery(ref)}`).then(r => ok<ObjectDetailDto>(r));

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
  return fetch(`${base}/data/${conn}?${refQuery(ref, query)}`).then(r => ok<DataPageDto>(r));
};

export const previewChanges = (conn: string, ref: string, changes: unknown[]): Promise<ChangePreviewDto> =>
  fetch(`${base}/data/${conn}/preview-changes?${refQuery(ref)}`, json("POST", { changes }))
    .then(r => ok<ChangePreviewDto>(r));

export const applyChanges = (conn: string, ref: string, hash: string): Promise<void> =>
  fetch(`${base}/data/${conn}/apply-changes?${refQuery(ref)}`, json("POST", { hash }))
    .then(r => ok<void>(r));

export const lookupValues = (conn: string, ref: string, column: string, search?: string):
  Promise<LookupItemDto[]> => {
  const query = new URLSearchParams({ column });
  if (search) query.set("search", search);
  return fetch(`${base}/data/${conn}/lookup?${refQuery(ref, query)}`)
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

export interface RunningOperationDto {
  id: string; kind: string; target: string; percentComplete: number | null;
  elapsedMs: number; statement: string | null;
}
export interface LockWaitDto {
  blocker: string; blocked: string; resource: string; waitMs: number; statement: string | null;
}
export interface ActivityDto { operations: RunningOperationDto[]; waits: LockWaitDto[] }
export interface ReplicaStateDto {
  name: string; role: string; state: string; lagBytes: number | null; lagSeconds: number | null;
}

/// What the server is doing right now, and who is waiting for whom. One call, because the overview
/// asks for both every few seconds.
export const serverActivity = (connectionId: string): Promise<ActivityDto> =>
  fetch(`${base}/admin/activity/${connectionId}`).then(r => ok<ActivityDto>(r));

export const replicationState = (connectionId: string): Promise<ReplicaStateDto[]> =>
  fetch(`${base}/admin/replication/${connectionId}`).then(r => ok<ReplicaStateDto[]>(r));

import type { TableDefinition } from "./designer/definition";

export interface DdlStatementDto { sql: string; destructive: boolean; description: string }
export interface DdlPreviewDto {
  hash: string; statements: DdlStatementDto[]; script: string;
  destructive: boolean; transactional: boolean;
}
export interface DdlLoadDto { definition: TableDefinition; create: string | null; supported: boolean }
export interface DependencyReportDto { dependsOn: string[]; usedBy: string[]; bestEffort: boolean }

export const loadDdl = (conn: string, ref: string): Promise<DdlLoadDto> =>
  fetch(`${base}/ddl/${conn}?${refQuery(ref)}`).then(r => ok<DdlLoadDto>(r));

export const previewDdl = (conn: string, ref: string | null, after: TableDefinition): Promise<DdlPreviewDto> =>
  fetch(`${base}/ddl/${conn}/preview`, json("POST", { objectRef: ref, after }))
    .then(r => ok<DdlPreviewDto>(r));

/// A statement the studio proposed — a fix from the health report — turned into the same previewed,
/// hashed change the table designer produces, so it goes through one path into the database.
export const previewScript = (conn: string, sql: string): Promise<DdlPreviewDto> =>
  fetch(`${base}/ddl/${conn}/script/preview`, json("POST", { sql }))
    .then(r => ok<DdlPreviewDto>(r));

export const applyScript = (conn: string, hash: string): Promise<void> =>
  fetch(`${base}/ddl/${conn}/apply`, json("POST", { hash })).then(r => ok<void>(r));

export const applyDdl = (conn: string, hash: string): Promise<void> =>
  fetch(`${base}/ddl/${conn}/apply`, json("POST", { hash })).then(r => ok<void>(r));

export const dependencies = (conn: string, ref: string): Promise<DependencyReportDto> =>
  fetch(`${base}/ddl/${conn}/dependencies?${refQuery(ref)}`)
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

// --- connection properties -------------------------------------------------------
export interface PropertyEntryDto { group: string; name: string; value: string }
export interface ConnectionPropertiesDto {
  /// The password is replaced by a mask; `revealConnectionString` returns the real one.
  connectionString: string;
  hasPassword: boolean;
  reachable: boolean;
  error: string | null;
  capabilities: Record<string, boolean>;
  properties: PropertyEntryDto[];
}

export const connectionProperties = (id: string): Promise<ConnectionPropertiesDto> =>
  fetch(`${base}/connections/${id}/properties`).then(r => ok<ConnectionPropertiesDto>(r));

/// Asked for on purpose, by a button the user presses — never on a routine page load.
export const revealConnectionString = (id: string): Promise<string> =>
  fetch(`${base}/connections/${id}/reveal`, { method: "POST" })
    .then(r => ok<{ connectionString: string }>(r))
    .then(body => body.connectionString);

// --- redis -----------------------------------------------------------------------------------
// Redis is browsed rather than queried, so it has endpoints of its own next to the command console.

export interface RedisKeyDto {
  key: string; type: string; ttlSeconds: number | null; sizeBytes: number | null; length: number | null;
}
export interface RedisKeyPageDto { keys: RedisKeyDto[]; nextCursor: number; complete: boolean }
export interface RedisValueDto {
  key: string; type: string; ttlSeconds: number | null; value: unknown; length: number;
  encoding: string | null;
}
export interface RedisPreviewDto { hash: string; commands: string[]; destructive: boolean }
export interface RedisBulkPreviewDto { hash: string; matchedKeys: number; sample: string[] }
export interface RedisPrefixStat { prefix: string; keys: number; bytes: number }
export interface RedisTypeStat { type: string; keys: number; bytes: number }
export interface RedisAnalysisDto {
  sampledKeys: number; complete: boolean;
  prefixes: RedisPrefixStat[]; types: RedisTypeStat[];
  largest: RedisKeyDto[]; expiringSoon: RedisKeyDto[];
  totalMemoryBytes: number | null; totalKeys: number | null;
}
export interface RedisStreamDto {
  length: number; firstId: string | null; lastId: string | null;
  groups: { name: string; consumers: number; pending: number; lastDelivered: string }[];
  pending: { id: string; consumer: string; idleMs: number; deliveryCount: number }[];
}
export interface RedisSlowEntryDto {
  id: number; at: string; microSeconds: number; command: string; client: string | null;
}

export const redisDatabases = (conn: string): Promise<{ database: number; keys: number }[]> =>
  fetch(`${base}/redis/${conn}/databases`).then(r => ok(r));

export const redisKeys = (conn: string, params: {
  db?: number; match?: string; type?: string; cursor?: number; count?: number; withSize?: boolean;
} = {}): Promise<RedisKeyPageDto> => {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params))
    if (value !== undefined && value !== "") query.set(key, String(value));

  return fetch(`${base}/redis/${conn}/keys?${query}`).then(r => ok<RedisKeyPageDto>(r));
};

export const redisValue = (conn: string, key: string, db?: number, offset = 0):
  Promise<RedisValueDto> => {
  const query = new URLSearchParams({ key, offset: String(offset) });
  if (db !== undefined) query.set("db", String(db));

  return fetch(`${base}/redis/${conn}/value?${query}`).then(r => ok<RedisValueDto>(r));
};

export const redisPreviewEdit = (conn: string, edit: {
  database: number; key: string; operation: string; payload: Record<string, unknown>;
}): Promise<RedisPreviewDto> =>
  fetch(`${base}/redis/${conn}/value/preview`, json("POST", edit)).then(r => ok<RedisPreviewDto>(r));

export const redisApplyEdit = (conn: string, hash: string): Promise<{ executed: number }> =>
  fetch(`${base}/redis/${conn}/value/apply`, json("POST", { hash })).then(r => ok(r));

export const redisPreviewBulk = (conn: string, request: {
  database: number; match: string; type?: string | null; action: string; ttlSeconds?: number | null;
}): Promise<RedisBulkPreviewDto> =>
  fetch(`${base}/redis/${conn}/bulk/preview`, json("POST", request))
    .then(r => ok<RedisBulkPreviewDto>(r));

export const redisApplyBulk = (conn: string, hash: string): Promise<{ affected: number }> =>
  fetch(`${base}/redis/${conn}/bulk/apply`, json("POST", { hash })).then(r => ok(r));

export const redisAnalysis = (conn: string, db?: number): Promise<RedisAnalysisDto> => {
  const query = new URLSearchParams();
  if (db !== undefined) query.set("db", String(db));

  return fetch(`${base}/redis/${conn}/analysis?${query}`).then(r => ok<RedisAnalysisDto>(r));
};

export const redisStream = (conn: string, key: string, db?: number): Promise<RedisStreamDto> => {
  const query = new URLSearchParams({ key });
  if (db !== undefined) query.set("db", String(db));

  return fetch(`${base}/redis/${conn}/stream?${query}`).then(r => ok<RedisStreamDto>(r));
};

export const redisSlowLog = (conn: string): Promise<RedisSlowEntryDto[]> =>
  fetch(`${base}/redis/${conn}/slowlog`).then(r => ok<RedisSlowEntryDto[]>(r));

export const redisPublish = (conn: string, channel: string, message: string):
  Promise<{ receivers: number }> =>
  fetch(`${base}/redis/${conn}/publish`, json("POST", { channel, message })).then(r => ok(r));

/// The subscription is server-sent events, so the browser's own EventSource carries it.
export const redisSubscribeUrl = (conn: string, channels: string) =>
  `${base}/redis/${conn}/subscribe?channels=${encodeURIComponent(channels)}`;
