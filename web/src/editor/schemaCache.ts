import { describeObject, listSchema, type SchemaNodeDto } from "../api";

export interface TableRef { name: string; ref: string; schema: string }

// Completion must not re-walk the schema on every keystroke; the tree is fetched once per
// connection and dropped when the explorer refreshes.
export class SchemaCache {
  private tablesByConnection = new Map<string, Promise<TableRef[]>>();
  private columnsByRef = new Map<string, Promise<string[]>>();

  invalidate(connectionId: string) {
    this.tablesByConnection.delete(connectionId);
    for (const key of [...this.columnsByRef.keys()])
      if (key.startsWith(`${connectionId}:`)) this.columnsByRef.delete(key);
  }

  tables(connectionId: string): Promise<TableRef[]> {
    let cached = this.tablesByConnection.get(connectionId);
    if (!cached) {
      cached = this.loadTables(connectionId).catch(() => []);
      this.tablesByConnection.set(connectionId, cached);
    }
    return cached;
  }

  async columns(connectionId: string, tableName: string): Promise<string[]> {
    const table = (await this.tables(connectionId))
      .find(t => t.name.toLowerCase() === tableName.toLowerCase());
    if (!table) return [];

    const key = `${connectionId}:${table.ref}`;
    let cached = this.columnsByRef.get(key);
    if (!cached) {
      cached = describeObject(connectionId, table.ref).then(d => d.columns.map(c => c.name)).catch(() => []);
      this.columnsByRef.set(key, cached);
    }
    return cached;
  }

  private async loadTables(connectionId: string): Promise<TableRef[]> {
    const out: TableRef[] = [];

    const walk = async (node: SchemaNodeDto, schema: string) => {
      if (node.kind === "Table" || node.kind === "View") {
        out.push({ name: node.label, ref: node.ref, schema });
        return;
      }
      if (!node.hasChildren) return;
      const children = await listSchema(connectionId, node.ref);
      await Promise.all(children.map(child =>
        walk(child, node.kind === "Schema" ? node.label : schema)));
    };

    const roots = await listSchema(connectionId);
    await Promise.all(roots.map(node => walk(node, node.kind === "Schema" ? node.label : "")));
    return out;
  }
}

export const schemaCache = new SchemaCache();
