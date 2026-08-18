export interface EngineField { key: "host" | "port" | "database" | "user" | "password" | "file"; label: string }
export interface EngineDef { id: string; label: string; defaultPort: number; fields: EngineField[] }

const server: EngineField[] = [
  { key: "host", label: "Host" },
  { key: "port", label: "Port" },
  { key: "database", label: "Database" },
  { key: "user", label: "User" },
  { key: "password", label: "Password" },
];
const file: EngineField[] = [{ key: "file", label: "File path" }];

export const ENGINES: EngineDef[] = [
  { id: "postgresql", label: "PostgreSQL", defaultPort: 5432, fields: server },
  { id: "mysql", label: "MySQL / MariaDB", defaultPort: 3306, fields: server },
  { id: "sqlserver", label: "SQL Server", defaultPort: 1433, fields: server },
  { id: "sqlite", label: "SQLite", defaultPort: 0, fields: file },
  { id: "oracle", label: "Oracle", defaultPort: 1521, fields: server },
  { id: "duckdb", label: "DuckDB", defaultPort: 0, fields: file },
  { id: "clickhouse", label: "ClickHouse", defaultPort: 8123, fields: server },
  { id: "mongodb", label: "MongoDB", defaultPort: 27017, fields: server },
  { id: "redis", label: "Redis", defaultPort: 6379, fields: server },
];

const URL_SCHEMES: Record<string, string> = {
  postgres: "postgresql", postgresql: "postgresql", mysql: "mysql", mariadb: "mysql",
  sqlserver: "sqlserver", mssql: "sqlserver", sqlite: "sqlite", oracle: "oracle",
  duckdb: "duckdb", clickhouse: "clickhouse", mongodb: "mongodb", redis: "redis",
};

// Lets a pasted connection string pick the engine, so the user does not have to.
export function engineFromConnectionString(text: string): string | null {
  const trimmed = text.trim();
  const scheme = trimmed.match(/^([a-z0-9+]+):\/\//i)?.[1]?.toLowerCase();
  if (scheme && URL_SCHEMES[scheme]) return URL_SCHEMES[scheme];

  const lower = trimmed.toLowerCase();
  if (lower.includes("initial catalog=")) return "sqlserver";
  if (lower.includes("host=") && lower.includes("username=")) return "postgresql";
  if (lower.includes("server=") && lower.includes("user id=")) return "sqlserver";
  if (lower.includes("server=") && lower.includes("user=")) return "mysql";
  if (lower.startsWith("data source=")) return "sqlite";
  return null;
}
