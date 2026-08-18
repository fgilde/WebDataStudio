const base = "/api";

export interface Me { anonymous: boolean; authenticated: boolean; username: string | null }
export interface Connection {
  id: string; name: string; engine: string; readOnly: boolean;
  color: string | null; group: string | null; source: "Environment" | "Stored"; summary: string;
}
export interface ConnectionInput {
  name: string; engine: string; connectionString: string;
  readOnly: boolean; color?: string | null; group?: string | null;
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
