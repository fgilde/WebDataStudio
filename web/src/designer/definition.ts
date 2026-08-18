export interface ColumnDefinition {
  name: string; type: string; nullable: boolean; default: string | null;
  identity: boolean; comment: string | null; renamedFrom?: string | null;
}
export interface IndexDefinition {
  name: string; columns: string[]; unique: boolean; filter?: string | null;
  includeColumns?: string[] | null;
}
export type ConstraintKind = "PrimaryKey" | "Unique" | "Check" | "ForeignKey";
export interface ConstraintDefinition {
  name: string; kind: ConstraintKind; columns: string[];
  expression?: string | null;
  referencedTable?: string | null; referencedColumns?: string[] | null;
  onDelete?: string; onUpdate?: string;
}
export interface TableDefinition {
  schema: string; name: string;
  columns: ColumnDefinition[];
  indexes: IndexDefinition[];
  constraints: ConstraintDefinition[];
  comment: string | null;
}

export const NEUTRAL_TYPES = [
  "text", "int", "bigint", "smallint", "bool", "float", "double", "decimal",
  "date", "timestamp", "uuid", "json", "blob",
];

export const emptyDefinition = (schema: string): TableDefinition => ({
  schema, name: "new_table",
  columns: [{ name: "id", type: "int", nullable: false, default: null, identity: true, comment: null }],
  indexes: [],
  constraints: [{ name: "pk_new_table", kind: "PrimaryKey", columns: ["id"] }],
  comment: null,
});

export const addColumn = (definition: TableDefinition): TableDefinition => ({
  ...definition,
  columns: [...definition.columns, {
    name: `column${definition.columns.length + 1}`, type: "text",
    nullable: true, default: null, identity: false, comment: null,
  }],
});

export const removeColumn = (definition: TableDefinition, index: number): TableDefinition => ({
  ...definition,
  columns: definition.columns.filter((_, i) => i !== index),
});

/// The first rename records where the column came from; a second rename keeps that origin, or the
/// diff against the database would look like a drop plus an add and lose the data.
export const renameColumn = (definition: TableDefinition, index: number, name: string): TableDefinition => ({
  ...definition,
  columns: definition.columns.map((c, i) => (i === index
    ? { ...c, name, renamedFrom: c.renamedFrom ?? c.name }
    : c)),
});

export const updateColumn = (
  definition: TableDefinition, index: number, patch: Partial<ColumnDefinition>,
): TableDefinition => ({
  ...definition,
  columns: definition.columns.map((c, i) => (i === index ? { ...c, ...patch } : c)),
});

export const moveColumn = (definition: TableDefinition, from: number, to: number): TableDefinition => {
  if (to < 0 || to >= definition.columns.length) return definition;
  const columns = [...definition.columns];
  const [moved] = columns.splice(from, 1);
  columns.splice(to, 0, moved);
  return { ...definition, columns };
};
