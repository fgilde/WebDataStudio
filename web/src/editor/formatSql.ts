import { format, type SqlLanguage } from "sql-formatter";
import type { DialectId } from "../sql/splitStatements";

const LANGUAGES: Record<DialectId, SqlLanguage> = {
  postgresql: "postgresql", mysql: "mysql", sqlserver: "tsql", sqlite: "sqlite",
  oracle: "plsql", duckdb: "postgresql", clickhouse: "sql",
};

export const formatSql = (sql: string, dialect: DialectId): string =>
  format(sql, { language: LANGUAGES[dialect] ?? "sql", keywordCase: "upper" });
