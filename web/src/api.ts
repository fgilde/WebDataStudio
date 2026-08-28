const base = "/api";

export interface Me {
  anonymous: boolean; authenticated: boolean; username: string | null;
  /// admin, editor or viewer. Null when the studio runs without accounts.
  role?: string | null;
  /// Name of this studio, from WDS_TITLE. Null when nothing named it.
  title?: string | null;
  /// The identity provider, where one is configured. `only` means there are no local accounts, so
  /// the login screen has nothing else to offer.
  sso?: { enabled: boolean; label: string; only: boolean };
}
export interface Connection {
  id: string; name: string; engine: string; readOnly: boolean;
  color: string | null; group: string | null; source: "Environment" | "Stored"; summary: string;
  tunnelled: boolean;
  /// This connection is one a person signs in to rather than one the machine can open on its own.
  interactive?: boolean;
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
  /// True when an assistance endpoint is configured. Absent on older servers.
  assist?: boolean;
  /// Whether a result can be shared as a link, and whether that link needs a login.
  share?: { isPublic: boolean } | null;
  /// Where the MCP endpoint is, when the studio has one. Null when nobody asked for one.
  /// `enabled` is false when it was asked for but refused — `reason` says why.
  mcp?: {
    path: string; writes: boolean; needsKey: boolean; enabled: boolean; reason: string | null;
  } | null;
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
  /// Whether the result was kept with the entry. The rows are fetched separately — a history list
  /// would otherwise carry every snapshot it ever took.
  hasSnapshot: boolean;
}
export interface HistoryInput {
  connectionId: string; sql: string;
  elapsedMs: number | null; rowCount: number | null; error: string | null;
  snapshot?: string;
}

export interface ResultSnapshot {
  columns: string[];
  rows: unknown[][];
  /// True when the result had more rows than the snapshot keeps.
  truncated: boolean;
}

export const historySnapshot = (id: number): Promise<ResultSnapshot> =>
  fetch(`${base}/history/${id}/snapshot`).then(r => ok<ResultSnapshot>(r));

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

/// One column of a data page. `masked` is the server saying it replaced the values; the grid offers a
/// reveal rather than leaving somebody wondering why a value looks like dots.
export interface DataColumnDto {
  name: string;
  dataType: string;
  nullable: boolean;
  masked?: boolean;
}

export interface DataPageDto {
  columns: DataColumnDto[];
  rows: unknown[][];
  editable: boolean;
  keyColumns: string[];
  reason: string | null;
  totalEstimate: number | null;
  offset: number;
  limit: number;
  /// Column names that came from the table a foreign key points at. Read-only: an edit here would
  /// be an update to a row this grid is not addressing.
  lookups?: string[];
}
export interface ChangePreviewDto {
  hash: string; script: string; statementCount: number; destructive: boolean;
}
export interface LookupItemDto { value: unknown; label: unknown }

export const browseData = (conn: string, ref: string,
  params: { offset?: number; limit?: number; sort?: string; desc?: boolean;
            filterColumn?: string; filter?: string; reveal?: boolean;
            /// "customer_id.name": a column from the table that foreign key points at.
            lookups?: string[] } = {}): Promise<DataPageDto> => {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (key === "lookups") continue;
    if (value !== undefined && value !== "") query.set(key, String(value));
  }
  // Repeated rather than joined: each one is its own value, and a column name may hold a comma.
  for (const lookup of params.lookups ?? []) query.append("lookup", lookup);
  return fetch(`${base}/data/${conn}?${refQuery(ref, query)}`).then(r => ok<DataPageDto>(r));
};

export interface StudioUserDto {
  name: string; role: string; connections: string[]; hashed: boolean;
}
export interface StudioUsersDto { anonymous: boolean; source: string; users: StudioUserDto[] }

export const listStudioUsers = (): Promise<StudioUsersDto> =>
  fetch(`${base}/admin/studio-users`).then(r => ok<StudioUsersDto>(r));

/// Turns a password into what WDS_USERS wants. Admin-only on the server.
export const hashStudioPassword = (password: string): Promise<{ hash: string }> =>
  fetch(`${base}/admin/studio-users/hash`, json("POST", { password }))
    .then(r => ok<{ hash: string }>(r));

export interface AssistReplyDto {
  text: string;
  statements: string[];
  /// Tools the model used to answer, when it had any. Naming them is what makes an answer
  /// checkable rather than something to believe.
  usedTools?: string[] | null;
}

export interface AssistCapabilitiesDto {
  configured: boolean; tools: boolean; toolNames: string[];
}

export const assistCapabilities = (): Promise<AssistCapabilitiesDto> =>
  fetch(`${base}/assist/capabilities`).then(r => ok<AssistCapabilitiesDto>(r));

/// A question the assistant may use the studio's own tools to answer.
export const assistAsk = (conn: string, question: string, includeSchema: boolean): Promise<AssistReplyDto> =>
  fetch(`${base}/assist/ask`, json("POST", { connectionId: conn, question, includeSchema }))
    .then(r => ok<AssistReplyDto>(r));

/// A conversation: the history belongs to the client, the system prompt and the tools to the
/// server. Nothing is kept server-side, so a restart loses no session and two tabs cannot collide.
export const assistChat = (conn: string,
  messages: { role: string; content: string }[], includeSchema: boolean): Promise<AssistReplyDto> =>
  fetch(`${base}/assist/chat`, json("POST", { connectionId: conn, messages, includeSchema }))
    .then(r => ok<AssistReplyDto>(r));

export interface ObjectStatisticsDto {
  supported: boolean;
  table: { name: string; value: string | null; kind: string }[];
  indexes: { name: string; sizeBytes: number | null; scans: number | null; unique: boolean; primary: boolean }[];
}

/// What the engine knows about one object beyond its shape. `supported: false` on an engine that
/// keeps no statistics — an empty list would read as "nothing to report".
export const objectStatistics = (conn: string, ref: string): Promise<ObjectStatisticsDto> =>
  fetch(`${base}/schema/${conn}/statistics?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<ObjectStatisticsDto>(r));

export interface ObjectPrivilegesDto {
  supported: boolean;
  grants: { grantee: string; privilege: string; grantable: boolean }[];
  privileges: string[];
}

export const objectPrivileges = (conn: string, ref: string): Promise<ObjectPrivilegesDto> =>
  fetch(`${base}/schema/${conn}/privileges?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<ObjectPrivilegesDto>(r));

/// The GRANT or REVOKE as text. Nothing runs here: it goes through the script preview.
export const privilegeStatement = (conn: string, ref: string, grantee: string, privilege: string,
  revoke: boolean): Promise<{ sql: string }> =>
  fetch(`${base}/schema/${conn}/privileges/statement?${refQuery(ref, new URLSearchParams())}`,
    json("POST", { grantee, privilege, revoke })).then(r => ok<{ sql: string }>(r));

export const objectDependencies = (conn: string, ref: string):
  Promise<{ dependsOn: string[]; usedBy: string[]; bestEffort: boolean }> =>
  fetch(`${base}/ddl/${conn}/dependencies?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<{ dependsOn: string[]; usedBy: string[]; bestEffort: boolean }>(r));

export const objectDdl = (conn: string, ref: string):
  Promise<{ create: string | null; supported: boolean }> =>
  fetch(`${base}/ddl/${conn}?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<{ create: string | null; supported: boolean }>(r));

export interface DistinctValuesDto {
  /// True when the column is masked: the distinct values of a column of secrets are the secrets.
  masked: boolean;
  values: { value: unknown; count: number }[];
  truncated: boolean;
}

export const distinctValues = (conn: string, ref: string, column: string, search?: string):
  Promise<DistinctValuesDto> => {
  const query = new URLSearchParams({ column });
  if (search) query.set("search", search);
  return fetch(`${base}/data/${conn}/distinct?${refQuery(ref, query)}`)
    .then(r => ok<DistinctValuesDto>(r));
};

export interface RowSecurityDto {
  supported: boolean;
  enabled: boolean;
  forced: boolean;
  policies: {
    name: string; command: string; roles: string; permissive: boolean;
    using: string | null; check: string | null;
  }[];
}

export const objectPolicies = (conn: string, ref: string): Promise<RowSecurityDto> =>
  fetch(`${base}/schema/${conn}/policies?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<RowSecurityDto>(r));

/// Every write here comes back as a statement: a policy is SQL, and reading it before it runs is
/// the point of the whole tab.
export const policyStatement = (conn: string, ref: string, body: {
  name: string; command?: string; roles?: string; using?: string; check?: string; drop?: boolean;
}): Promise<{ sql: string }> =>
  fetch(`${base}/schema/${conn}/policies/statement?${refQuery(ref, new URLSearchParams())}`,
    json("POST", body)).then(r => ok<{ sql: string }>(r));

export const securityStatement = (conn: string, ref: string, enable: boolean, force: boolean):
  Promise<{ sql: string }> =>
  fetch(`${base}/schema/${conn}/policies/security-statement?${refQuery(ref, new URLSearchParams())}`,
    json("POST", { enable, force })).then(r => ok<{ sql: string }>(r));

export interface PartitioningDto {
  supported: boolean;
  partitioned: boolean;
  strategy: string | null;
  key: string | null;
  partitions: { name: string; bound: string; sizeBytes: number | null; rows: number | null }[];
}

export const objectPartitions = (conn: string, ref: string): Promise<PartitioningDto> =>
  fetch(`${base}/schema/${conn}/partitions?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<PartitioningDto>(r));

export const partitionStatement = (conn: string, ref: string, body: {
  partition: string; bound?: string; detach?: boolean; concurrently?: boolean;
}): Promise<{ sql: string }> =>
  fetch(`${base}/schema/${conn}/partitions/statement?${refQuery(ref, new URLSearchParams())}`,
    json("POST", body)).then(r => ok<{ sql: string }>(r));

/// Refreshing a materialised view. Concurrently keeps it readable and needs a unique index.
export const refreshStatement = (conn: string, ref: string, concurrently: boolean):
  Promise<{ sql: string }> =>
  fetch(`${base}/schema/${conn}/refresh-statement?${refQuery(ref, new URLSearchParams())}`,
    json("POST", { concurrently })).then(r => ok<{ sql: string }>(r));

/// "SELECT on everything in this schema for that role" — one script rather than one dialog per
/// table.
export const bulkGrantStatement = (conn: string, body: {
  schema: string; grantee: string; privileges: string[]; revoke?: boolean; includeFuture?: boolean;
}): Promise<{ sql: string; tables: number }> =>
  fetch(`${base}/schema/${conn}/privileges/bulk-statement`, json("POST", body))
    .then(r => ok<{ sql: string; tables: number }>(r));

export interface FunctionInfoDto {
  supported: boolean;
  language: string | null;
  returns: string | null;
  returnsSet: boolean;
  arguments: { name: string; type: string; mode: string; hasDefault: boolean }[];
  source: string | null;
}

export const functionInfo = (conn: string, ref: string): Promise<FunctionInfoDto> =>
  fetch(`${base}/schema/${conn}/function?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<FunctionInfoDto>(r));

export interface TrialRunDto {
  columns: string[];
  rows: unknown[][];
  notices: string[];
  elapsedMs: number;
  truncated: boolean;
}

/// Runs the function inside a transaction the server always rolls back. Not a debugger: no
/// stepping, no breakpoints — the source, the arguments, what came back and what it raised.
export const functionTrialRun = (conn: string, ref: string, args: (string | null)[]):
  Promise<TrialRunDto> =>
  fetch(`${base}/schema/${conn}/function/run?${refQuery(ref, new URLSearchParams())}`,
    json("POST", { arguments: args })).then(r => ok<TrialRunDto>(r));

export interface SharedResultDto {
  id: string;
  connectionName: string;
  sql: string;
  by: string | null;
  at: string;
  expiresAt: string;
  columns: string[];
  rows: (string | null)[][];
  truncated: boolean;
}

/// A snapshot of a result, by its link id. Open without a login when the studio says so.
export const sharedResult = (id: string): Promise<SharedResultDto> =>
  fetch(`${base}/share/${id}`).then(r => ok<SharedResultDto>(r));

export interface ShareCreatedDto {
  id: string; url: string; expiresAt: string; rows: number; truncated: boolean; isPublic: boolean;
}

/// Keeps this result's rows and hands back a link to them.
export const shareResult = (conn: string, sql: string): Promise<ShareCreatedDto> =>
  fetch(`${base}/share`, json("POST", { connectionId: conn, sql })).then(r => ok<ShareCreatedDto>(r));

export const shareSettings = (): Promise<{ enabled: boolean; isPublic: boolean }> =>
  fetch(`${base}/share`).then(r => ok<{ enabled: boolean; isPublic: boolean }>(r));

export interface McpInfoDto {
  name: string;
  protocolVersion: string;
  writes: boolean;
  authentication: string;
  tools: { name: string; description: string; writes: boolean }[];
}

/// The MCP endpoint describes itself on a GET. It lives outside /api, so it takes its own path —
/// and when the studio refuses to serve it, that path falls through to the SPA and answers HTML.
/// Saying so beats "Unexpected token '<'".
export const mcpInfo = async (path: string): Promise<McpInfoDto> => {
  const response = await fetch(path, { headers: { accept: "application/json" } });
  const text = await response.text();

  try {
    return JSON.parse(text) as McpInfoDto;
  } catch {
    throw new Error(
      `${path} did not answer as JSON (HTTP ${response.status}). The endpoint is not being served — `
      + "check the studio's log for the reason.");
  }
};

/// Both calls answer 501 when no assistance endpoint is configured, which is how the UI knows not
/// to offer them at all.
export const assistExplain = (conn: string, sql: string, includeSchema: boolean): Promise<AssistReplyDto> =>
  fetch(`${base}/assist/explain`, json("POST", { connectionId: conn, sql, includeSchema }))
    .then(r => ok<AssistReplyDto>(r));

export const assistSql = (conn: string, question: string, includeSchema: boolean): Promise<AssistReplyDto> =>
  fetch(`${base}/assist/sql`, json("POST", { connectionId: conn, question, includeSchema }))
    .then(r => ok<AssistReplyDto>(r));

export interface GenerateStrategiesDto {
  available: string[];
  columns: { name: string; dataType: string; nullable: boolean; strategy: string }[];
}

export const generateStrategies = (conn: string, ref: string): Promise<GenerateStrategiesDto> =>
  fetch(`${base}/data/${conn}/generate/strategies?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<GenerateStrategiesDto>(r));

/// Generated rows are previewed as the inserts they are; applying goes through applyChanges.
export const previewGenerate = (conn: string, ref: string,
  body: { rows: number; seed?: number; strategies?: Record<string, string> },
): Promise<ChangePreviewDto & { emptyForeignKeys?: string[] }> =>
  fetch(`${base}/data/${conn}/generate/preview?${refQuery(ref, new URLSearchParams())}`,
    json("POST", body)).then(r => ok<ChangePreviewDto & { emptyForeignKeys?: string[] }>(r));

export interface UndoStateDto { available: boolean; label: string | null; at: string | null }

export const getUndoState = (conn: string, ref: string): Promise<UndoStateDto> =>
  fetch(`${base}/data/${conn}/undo?${refQuery(ref, new URLSearchParams())}`)
    .then(r => ok<UndoStateDto>(r));

/// The inverse of the last change, as a script to approve. Applying it goes through the same
/// apply-changes call every other change uses.
export const previewUndo = (conn: string, ref: string): Promise<ChangePreviewDto> =>
  fetch(`${base}/data/${conn}/undo/preview?${refQuery(ref, new URLSearchParams())}`,
    { method: "POST" }).then(r => ok<ChangePreviewDto>(r));

export interface MaskPolicyDto { maskByDefault: boolean; extra: string[]; never: string[] }

export const getMaskPolicy = (conn: string): Promise<MaskPolicyDto> =>
  fetch(`${base}/data/${conn}/mask-policy`).then(r => ok<MaskPolicyDto>(r));

export const saveMaskPolicy = (conn: string, policy: MaskPolicyDto): Promise<void> =>
  fetch(`${base}/data/${conn}/mask-policy`, {
    method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify(policy),
  }).then(r => { if (!r.ok) throw new Error("could not save the mask policy"); });

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

// --- archives ----------------------------------------------------------------
export interface ArchiveInfoDto {
  name: string;
  columns: { name: string; dataType: string }[];
  rows: number;
  sizeBytes: number;
  savedAt: string;
  source: string | null;
}

export interface ArchiveListDto {
  available: boolean;
  path: string;
  error: string | null;
  items: ArchiveInfoDto[];
}

export const listArchives = (): Promise<ArchiveListDto> =>
  fetch(`${base}/archives`).then(r => ok<ArchiveListDto>(r));

export interface ArchivePageDto {
  columns: { name: string; dataType: string }[];
  rows: unknown[][];
  total: number;
  offset: number;
}

export const readArchive = (name: string, offset = 0, limit = 200): Promise<ArchivePageDto> =>
  fetch(`${base}/archives/${encodeURIComponent(name)}?offset=${offset}&limit=${limit}`)
    .then(r => ok<ArchivePageDto>(r));

/// Keeps a statement's result, or a whole table, as a file on the studio's own disk. Masked
/// columns are masked on the way in: an archive of them would be a way around the masking.
export const saveArchive = (name: string, body: {
  connectionId: string; sql?: string; objectRef?: string; maxRows?: number;
}): Promise<ArchiveInfoDto> =>
  fetch(`${base}/archives/${encodeURIComponent(name)}`, json("POST", body))
    .then(r => ok<ArchiveInfoDto>(r));

export const deleteArchive = (name: string): Promise<void> =>
  fetch(`${base}/archives/${encodeURIComponent(name)}`, { method: "DELETE" }).then(r => ok<void>(r));

/// The rows again as INSERT statements, for wherever they should end up next.
export const archiveInsertScript = (name: string, connectionId: string, table: string, limit?: number):
  Promise<{ sql: string; rows: number; truncated: boolean }> => {
  const query = new URLSearchParams({ connectionId, table });
  if (limit) query.set("limit", String(limit));
  return fetch(`${base}/archives/${encodeURIComponent(name)}/insert-script?${query}`,
    { method: "POST" }).then(r => ok<{ sql: string; rows: number; truncated: boolean }>(r));
};

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

export interface DiagramDto { nodes: DiagramNodeDto[]; edges: DiagramEdgeDto[] }

export const loadDiagram = (conn: string, schema?: string, refresh = false):
  Promise<DiagramDto> => {
  const query = new URLSearchParams();
  if (schema) query.set("schema", schema);
  if (refresh) query.set("refresh", "true");

  return fetch(`${base}/diagram/${conn}${query.size ? `?${query}` : ""}`)
    .then(r => ok<DiagramDto>(r));
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

export interface ConnectionPresetDto {
  id: string; label: string; engine: string; template: string; description: string;
  /// Opening it needs a person to sign in — the device-code flow — rather than the machine's identity.
  interactive: boolean;
}

export const connectionPresets = (engine?: string): Promise<ConnectionPresetDto[]> =>
  fetch(`${base}/connection-presets${engine ? `?engine=${encodeURIComponent(engine)}` : ""}`)
    .then(r => ok<ConnectionPresetDto[]>(r));

export interface EntraStatusDto {
  /// none, starting, pending, signed-in, expired or failed. The token never leaves the server.
  state: string;
  userCode: string | null;
  verificationUrl: string | null;
  message: string | null;
  expiresOn: string | null;
  error: string | null;
}

export const entraSignIn = (conn: string, tenant?: string): Promise<EntraStatusDto> =>
  fetch(`${base}/connections/${conn}/entra/signin${tenant ? `?tenant=${encodeURIComponent(tenant)}` : ""}`,
    { method: "POST" }).then(r => ok<EntraStatusDto>(r));

export const entraStatus = (conn: string): Promise<EntraStatusDto> =>
  fetch(`${base}/connections/${conn}/entra`).then(r => ok<EntraStatusDto>(r));

export const entraSignOut = (conn: string): Promise<void> =>
  fetch(`${base}/connections/${conn}/entra`, { method: "DELETE" }).then(r => ok<void>(r));

export interface ExportTemplateDto {
  id: string; label: string; extension: string; contentType: string;
  header: string | null; row: string; footer: string | null; separator: string;
}

export const exportTemplates = (): Promise<{ templates: ExportTemplateDto[]; error: string | null }> =>
  fetch(`${base}/export/templates`)
    .then(r => ok<{ templates: ExportTemplateDto[]; error: string | null }>(r));

export const saveExportTemplate = (template: ExportTemplateDto): Promise<ExportTemplateDto> =>
  fetch(`${base}/export/templates`, json("PUT", template)).then(r => ok<ExportTemplateDto>(r));

export const deleteExportTemplate = (id: string): Promise<void> =>
  fetch(`${base}/export/templates/${encodeURIComponent(id)}`, { method: "DELETE" })
    .then(r => ok<void>(r));

export interface SchemaScopeDto {
  /// Every schema the connection could read.
  available: string[];
  /// The ones this studio chose, empty meaning all of them.
  chosen: string[];
  /// Where the deployment fixed the scope, that list; then `editable` is false.
  fixedByEnvironment: string[];
  editable: boolean;
}

export const schemaScope = (conn: string): Promise<SchemaScopeDto> =>
  fetch(`${base}/schema/${conn}/scope`).then(r => ok<SchemaScopeDto>(r));

export const chooseSchemas = (conn: string, schemas: string[]): Promise<{ chosen: string[] }> =>
  fetch(`${base}/schema/${conn}/scope`, json("PUT", schemas)).then(r => ok<{ chosen: string[] }>(r));

export interface DataHitDto {
  schema: string; table: string; column: string; dataType: string; matches: number;
}
export interface DataSearchDto {
  hits: DataHitDto[];
  tablesSearched: number;
  tablesSkipped: number;
  /// Tables that could not be searched, with the reason.
  notes: string[];
  truncated: boolean;
}

/// "Find this value in any table", server-side and type-aware.
export const searchData = (
  conn: string, value: string, options?: { schema?: string; exact?: boolean; maxTables?: number },
): Promise<DataSearchDto> => {
  const query = new URLSearchParams({ value });
  if (options?.schema) query.set("schema", options.schema);
  if (options?.exact) query.set("exact", "true");
  if (options?.maxTables) query.set("maxTables", String(options.maxTables));

  return fetch(`${base}/search/${conn}/data?${query}`).then(r => ok<DataSearchDto>(r));
};

export interface TableSizeDto {
  schema: string; table: string; bytes: number; rows: number | null;
}
export interface TableGrowthDto {
  schema: string; table: string;
  firstBytes: number; lastBytes: number;
  from: string; to: string;
  rows: number | null;
  delta: number;
  /// Null for a table that started at nothing, where a percentage would be true and useless.
  percent: number | null;
  perDay: number;
}
export interface SizesDto {
  available: boolean;
  reason: string | null;
  days?: number;
  tables: TableSizeDto[];
  growth: TableGrowthDto[];
}

/// How big every table is — and, once there are two samples, how much bigger than it was. Asking
/// records a sample, so the history builds itself.
export const tableSizes = (conn: string, days?: number): Promise<SizesDto> =>
  fetch(`${base}/admin/sizes/${conn}${days ? `?days=${days}` : ""}`).then(r => ok<SizesDto>(r));

export interface StatementStatsDto {
  fingerprint: string;
  example: string;
  runs: number;
  failures: number;
  averageMs: number;
  slowestMs: number;
  fastestMs: number;
  firstSeen: string;
  lastSeen: string;
  /// Recent runs against older ones, as a factor. Null where there is not enough history.
  trend: number | null;
}

export const historyStats = (options?: { connectionId?: string; days?: number; top?: number }):
  Promise<{ days: number; runs: number; statements: StatementStatsDto[] }> => {
  const query = new URLSearchParams();
  if (options?.connectionId) query.set("connectionId", options.connectionId);
  if (options?.days) query.set("days", String(options.days));
  if (options?.top) query.set("top", String(options.top));

  return fetch(`${base}/history/stats?${query}`)
    .then(r => ok<{ days: number; runs: number; statements: StatementStatsDto[] }>(r));
};

export interface ImportColumnDto { name: string; sourceType: string; targetType: string }

export interface ImportPlanDto {
  schema: string;
  table: string;
  columns: ImportColumnDto[];
  /// The CREATE TABLE that will run, for reading before it does.
  createSql: string;
  /// Where the reader can say so without reading the whole file.
  rows: number | null;
  preview: (string | null)[][];
}

export interface ImportOutcomeDto { table: string; rows: number; createSql: string }

/// A file becomes a table: `apply: false` plans it, `apply: true` creates and loads it.
export const importFileAsTable = (conn: string, options: {
  table: string;
  schema?: string;
  apply: boolean;
  file?: File | null;
  /// An object in a bucket, read where it is rather than downloaded first.
  source?: { storageConnection: string; objectRef: string };
}): Promise<ImportPlanDto | ImportOutcomeDto> => {
  const query = new URLSearchParams({ table: options.table });
  if (options.schema) query.set("schema", options.schema);
  if (options.apply) query.set("apply", "true");

  if (options.source) {
    query.set("storageConnection", options.source.storageConnection);
    query.set("ref", options.source.objectRef);

    return fetch(`${base}/import/${conn}/new-table?${query}`, { method: "POST" })
      .then(r => ok<ImportPlanDto | ImportOutcomeDto>(r));
  }

  const body = new FormData();
  if (options.file) body.append("file", options.file);

  return fetch(`${base}/import/${conn}/new-table?${query}`, { method: "POST", body })
    .then(r => ok<ImportPlanDto | ImportOutcomeDto>(r));
};

export interface JsonPathDto {
  path: string;
  /// Every type seen at this path. More than one is where a flatten breaks.
  types: string[];
  present: number;
  example: string | null;
  /// The SQL that reads this path on this engine.
  expression: string;
}
export interface JsonShapeDto {
  sampled: number;
  parsed: number;
  note: string | null;
  paths: JsonPathDto[];
  /// The SELECT that turns the paths into columns.
  flatten: string;
}

export const jsonShape = (conn: string, ref: string, column: string,
  sample?: number): Promise<JsonShapeDto> =>
  fetch(`${base}/data/${conn}/json?${refQuery(ref, new URLSearchParams({
    column, ...(sample ? { sample: String(sample) } : {}),
  }))}`).then(r => ok<JsonShapeDto>(r));

export interface SqlFindingDto {
  id: string;
  /// warning for what is probably a mistake, note for what is merely worth knowing.
  severity: string;
  message: string;
  statement: number;
  line: number;
  excerpt: string;
}

/// A read of the SQL before it runs. It warns; it never refuses.
export const inspectSql = (conn: string, sql: string): Promise<SqlFindingDto[]> =>
  fetch(`${base}/query/inspect`, json("POST", { connectionId: conn, sql }))
    .then(r => ok<SqlFindingDto[]>(r));

export interface CapturedStatementDto {
  text: string; samples: number; maxDurationMs: number;
  firstSeen: string; lastSeen: string;
  sessions: string[]; users: string[]; databases: string[]; blocked: boolean;
}
export interface CaptureDto {
  /// none, running, done, stopped or failed.
  state: string;
  startedAt: string | null;
  seconds: number;
  secondsLeft: number;
  samples: number;
  statements: CapturedStatementDto[];
  error: string | null;
}

export const startCapture = (conn: string, seconds: number): Promise<CaptureDto> =>
  fetch(`${base}/admin/capture/${conn}?seconds=${seconds}`, { method: "POST" })
    .then(r => ok<CaptureDto>(r));

export const captureStatus = (conn: string): Promise<CaptureDto> =>
  fetch(`${base}/admin/capture/${conn}`).then(r => ok<CaptureDto>(r));

export const stopCapture = (conn: string): Promise<CaptureDto> =>
  fetch(`${base}/admin/capture/${conn}`, { method: "DELETE" }).then(r => ok<CaptureDto>(r));

export interface CaptureAdviceDto {
  table: string;
  message: string;
  /// The statement to run, where the advice is one.
  sql: string | null;
  statements: number;
  samples: number;
  slowestMs: number;
  example: string;
}

/// What the captured minute suggests: the capture and the index advisor together.
export const captureAdvice = (conn: string):
  Promise<{ state: string; reason: string | null; advice: CaptureAdviceDto[] }> =>
  fetch(`${base}/admin/capture/${conn}/advice`)
    .then(r => ok<{ state: string; reason: string | null; advice: CaptureAdviceDto[] }>(r));

export interface JobDto {
  id: string; name: string; enabled: boolean; schedule: string;
  lastRun: string | null; lastOutcome: string | null; nextRun: string | null;
  command: string | null;
}
export interface JobRunDto {
  started: string | null; finished: string | null; outcome: string;
  durationMs: number | null; message: string | null;
}
export interface JobsDto {
  available: boolean;
  /// What this engine calls its scheduler: SQL Server Agent, pg_cron, events.
  scheduler: string | null;
  reason: string | null;
  jobs: JobDto[];
  actions: { id: string; label: string; destructive: boolean }[];
}

export const listJobs = (conn: string): Promise<JobsDto> =>
  fetch(`${base}/admin/jobs/${conn}`).then(r => ok<JobsDto>(r));

export const jobHistory = (conn: string, id: string): Promise<JobRunDto[]> =>
  fetch(`${base}/admin/jobs/${conn}/history?id=${encodeURIComponent(id)}`)
    .then(r => ok<JobRunDto[]>(r));

/// Changing a job comes back as SQL: it goes through the editor's run like every other change.
export const jobStatement = (conn: string, id: string, action: string): Promise<{ sql: string }> =>
  fetch(`${base}/admin/jobs/${conn}/statement`, json("POST", { id, action }))
    .then(r => ok<{ sql: string }>(r));

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
export interface BackupOptionsInput {
  schemaOnly?: boolean; dataOnly?: boolean; tables?: string[];
  /// pg_dump only: plain, custom or tar. The server refuses it on the other engines rather than
  /// producing a plain dump under a misleading name.
  format?: string; noOwner?: boolean; clean?: boolean; compress?: number;
}

export const downloadBackup = async (conn: string, body: BackupOptionsInput,
  onBytes?: (written: number) => void): Promise<void> => {
  const response = await fetch(`${base}/admin/backup/${conn}`, json("POST", body));
  if (!response.ok) await fail(response);

  // A dump has no length up front — the tool is still running. Counting what arrives is the only
  // progress there is, and it is the one worth showing.
  const blob = onBytes && response.body
    ? await countingBlob(response.body, response.headers.get("content-type"), onBytes)
    : await response.blob();
  const disposition = response.headers.get("content-disposition") ?? "";
  const name = /filename="([^"]+)"/.exec(disposition)?.[1] ?? "backup";

  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
};

async function countingBlob(body: ReadableStream<Uint8Array>, contentType: string | null,
  onBytes: (written: number) => void): Promise<Blob> {
  const reader = body.getReader();
  const chunks: BlobPart[] = [];
  let written = 0;

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value as BlobPart);
    written += value.byteLength;
    onBytes(written);
  }

  return new Blob(chunks, { type: contentType ?? "application/octet-stream" });
}

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
export interface RedisCommandDto {
  name: string; arity: number; summary: string; group: string; since: string;
}
export interface RedisClusterDto {
  enabled: boolean; state: string; knownNodes: number;
  nodes: { id: string; endpoint: string; role: string; slots: string; connected: boolean }[];
}

/// What this server says it can do, from COMMAND DOCS (or COMMAND INFO on an older one).
export const redisCommands = (conn: string): Promise<RedisCommandDto[]> =>
  fetch(`${base}/redis/${conn}/commands`)
    .then(r => ok<{ commands: RedisCommandDto[] }>(r)).then(b => b.commands);

export const redisCluster = (conn: string): Promise<RedisClusterDto> =>
  fetch(`${base}/redis/${conn}/cluster`).then(r => ok<RedisClusterDto>(r));

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

export interface StoragePreviewDto {
  name: string;
  key: string;
  contentType: string | null;
  size: number;
  modified: string | null;
  etag: string | null;
  storageClass: string | null;
  /// Whether a reader here understands the file, and the data tab can therefore open it.
  queryable: boolean;
  /// What a query selects from — `read_parquet('s3://…')` — or null where nothing reads it.
  from: string | null;
  /// The provider's own URI for this object, for copying.
  uri: string;
  truncated: boolean;
  text: string | null;
  binary: boolean;
}

export const previewObject = (conn: string, ref: string): Promise<StoragePreviewDto> =>
  fetch(`${base}/storage/${conn}/preview?${refQuery(ref)}`).then(r => ok<StoragePreviewDto>(r));

/// A download and an image both need a URL rather than a promise: one goes to a link, the other to
/// an `img` tag.
export const objectUrl = (conn: string, ref: string) =>
  `${base}/storage/${conn}/download?${refQuery(ref)}`;

/// A whole prefix as one zip. Streamed, so the response has no length and the limits are written
/// into the archive itself where they had to stop the walk.
export const archiveUrl = (conn: string, ref: string) =>
  `${base}/storage/${conn}/archive?${refQuery(ref)}`;

export const uploadObject = (conn: string, ref: string, file: File): Promise<{ key: string }> =>
  fetch(`${base}/storage/${conn}/upload?${refQuery(ref, new URLSearchParams({ name: file.name }))}`,
    { method: "POST", headers: { "content-type": file.type || "application/octet-stream" }, body: file })
    .then(r => ok<{ key: string }>(r));

export const deleteObject = (conn: string, ref: string): Promise<{ key: string }> =>
  fetch(`${base}/storage/${conn}?${refQuery(ref)}`, { method: "DELETE" })
    .then(r => ok<{ key: string }>(r));

/// One rule somebody wrote about their data. The kind decides what `argument` means: a range is
/// `0..100`, a reference `other_table.column`, a freshness `24h`, an expression the condition a bad
/// row satisfies.
export interface QualityRuleDto {
  id: string;
  connectionId: string;
  schema: string;
  table: string;
  column: string;
  kind: "NotNull" | "Unique" | "Range" | "Referential" | "Freshness" | "Expression";
  argument: string | null;
  message: string | null;
  enabled: boolean;
}
export interface QualityResultDto {
  rule: QualityRuleDto;
  violations: number;
  statement: string;
  ranAt: string;
  error: string | null;
}

export const qualityRules = (conn: string): Promise<QualityRuleDto[]> =>
  fetch(`${base}/quality/${conn}`).then(r => ok<QualityRuleDto[]>(r));

export const saveQualityRule = (conn: string, rule: QualityRuleDto): Promise<QualityRuleDto> =>
  fetch(`${base}/quality/${conn}`, json("PUT", rule)).then(r => ok<QualityRuleDto>(r));

export const deleteQualityRule = (conn: string, id: string): Promise<void> =>
  fetch(`${base}/quality/${conn}/${id}`, { method: "DELETE" }).then(r => ok<void>(r));

/// Runs every enabled rule and answers with what each one counted.
export const runQualityRules = (conn: string):
  Promise<{ ran: number; failing: number; results: QualityResultDto[] }> =>
  fetch(`${base}/quality/${conn}/run`, { method: "POST" })
    .then(r => ok<{ ran: number; failing: number; results: QualityResultDto[] }>(r));

/// One line of the audit trail: who asked for what, and what came of it.
export interface AuditEntryDto {
  id: number;
  at: string;
  user: string;
  role: string;
  connectionId: string;
  action: string;
  detail: string;
  status: number;
  elapsedMs: number;
  address: string;
}

/// Who did what through this studio. Admin-only, like the rest of /api/admin.
export const auditTrail = (query: { user?: string; conn?: string; search?: string; limit?: number }):
  Promise<{ enabled: boolean; entries: AuditEntryDto[] }> => {
  const params = new URLSearchParams();
  if (query.user) params.set("user", query.user);
  if (query.conn) params.set("conn", query.conn);
  if (query.search) params.set("search", query.search);
  if (query.limit) params.set("limit", String(query.limit));

  return fetch(`${base}/admin/audit?${params}`)
    .then(r => ok<{ enabled: boolean; entries: AuditEntryDto[] }>(r));
};

/// One table in a development subset.
export interface SubsetTableDto {
  schema: string; name: string; rows: number; statement: string;
}
export interface SubsetResultDto {
  script: string;
  tables: SubsetTableDto[];
  rows: number;
  /// What the subset could not do, in its own words: a multi-column foreign key it left out, a
  /// cycle it had to break, a table it stopped at.
  notes: string[];
}

/// A small, loadable, anonymised copy of a real database: these rows and the rows they point at.
export const buildSubset = (conn: string, request: {
  table: string; schema?: string; where?: string; rows?: number;
  includeSchema?: boolean; anonymise?: boolean; depth?: number;
}): Promise<SubsetResultDto> =>
  fetch(`${base}/export/subset/${conn}`, json("POST", request)).then(r => ok<SubsetResultDto>(r));
