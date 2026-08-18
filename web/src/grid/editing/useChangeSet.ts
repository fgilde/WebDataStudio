import { useCallback, useMemo, useState } from "react";

export type CellState = "clean" | "edited" | "inserted" | "deleted";

export interface RowChange {
  kind: "insert" | "update" | "delete";
  key: Record<string, unknown>;
  values: Record<string, unknown>;
}

interface Edit { rowIndex: number; column: string; value: unknown }

export interface ChangeSetApi {
  changes: RowChange[];
  isDirty: boolean;
  insertedRows: number;
  edit: (rowIndex: number, column: string, value: unknown) => void;
  insertRow: (values?: Record<string, unknown>) => void;
  deleteRow: (rowIndex: number) => void;
  duplicateRow: (rowIndex: number, values: Record<string, unknown>) => void;
  revert: (rowIndex: number) => void;
  revertAll: () => void;
  cellState: (rowIndex: number, column: string) => CellState;
  editedValue: (rowIndex: number, column: string) => unknown;
}

/// Tracks pending edits for one result. Rows are addressed by their index in the fetched page;
/// inserted rows get negative indexes so they never collide with a real row.
export function useChangeSet(
  keyColumns: string[],
  columns: string[],
  rowAt: (index: number) => unknown[] | undefined,
): ChangeSetApi {
  const [edits, setEdits] = useState<Edit[]>([]);
  const [deleted, setDeleted] = useState<number[]>([]);
  const [inserted, setInserted] = useState<{ index: number; values: Record<string, unknown> }[]>([]);

  const originalValue = useCallback((rowIndex: number, column: string) => {
    const row = rowAt(rowIndex);
    const position = columns.indexOf(column);
    return row && position >= 0 ? row[position] : undefined;
  }, [columns, rowAt]);

  const edit = useCallback((rowIndex: number, column: string, value: unknown) => {
    if (rowIndex < 0) {
      setInserted(list => list.map(r => (r.index === rowIndex ? { ...r, values: { ...r.values, [column]: value } } : r)));
      return;
    }

    setEdits(list => {
      const rest = list.filter(e => !(e.rowIndex === rowIndex && e.column === column));
      // Editing a cell back to its original value is not a change at all.
      const original = originalValue(rowIndex, column);
      return sameValue(original, value) ? rest : [...rest, { rowIndex, column, value }];
    });
  }, [originalValue]);

  const insertRow = useCallback((values: Record<string, unknown> = {}) => {
    setInserted(list => [...list, { index: -(list.length + 1), values }]);
  }, []);

  const duplicateRow = useCallback((_rowIndex: number, values: Record<string, unknown>) => {
    // The key is cleared: a duplicate is a new row, not a second copy of the same one.
    const copy = { ...values };
    for (const key of keyColumns) delete copy[key];
    setInserted(list => [...list, { index: -(list.length + 1), values: copy }]);
  }, [keyColumns]);

  const deleteRow = useCallback((rowIndex: number) => {
    if (rowIndex < 0) { setInserted(list => list.filter(r => r.index !== rowIndex)); return; }
    setDeleted(list => (list.includes(rowIndex) ? list : [...list, rowIndex]));
  }, []);

  const revert = useCallback((rowIndex: number) => {
    setEdits(list => list.filter(e => e.rowIndex !== rowIndex));
    setDeleted(list => list.filter(i => i !== rowIndex));
    setInserted(list => list.filter(r => r.index !== rowIndex));
  }, []);

  const revertAll = useCallback(() => { setEdits([]); setDeleted([]); setInserted([]); }, []);

  const cellState = useCallback((rowIndex: number, column: string): CellState => {
    if (rowIndex < 0) return "inserted";
    if (deleted.includes(rowIndex)) return "deleted";
    return edits.some(e => e.rowIndex === rowIndex && e.column === column) ? "edited" : "clean";
  }, [deleted, edits]);

  const editedValue = useCallback((rowIndex: number, column: string) => {
    if (rowIndex < 0) return inserted.find(r => r.index === rowIndex)?.values[column];
    const edit = edits.find(e => e.rowIndex === rowIndex && e.column === column);
    return edit ? edit.value : originalValue(rowIndex, column);
  }, [edits, inserted, originalValue]);

  const changes = useMemo<RowChange[]>(() => {
    const keyFor = (rowIndex: number) =>
      Object.fromEntries(keyColumns.map(k => [k, originalValue(rowIndex, k)]));

    // A row that is both edited and deleted only needs the delete.
    const updates = [...new Set(edits.map(e => e.rowIndex))]
      .filter(rowIndex => !deleted.includes(rowIndex))
      .map(rowIndex => ({
        kind: "update" as const,
        key: keyFor(rowIndex),
        values: Object.fromEntries(
          edits.filter(e => e.rowIndex === rowIndex).map(e => [e.column, e.value])),
      }));

    const deletes = deleted.map(rowIndex => ({
      kind: "delete" as const, key: keyFor(rowIndex), values: {},
    }));

    const inserts = inserted.map(row => ({
      kind: "insert" as const, key: {}, values: row.values,
    })).filter(row => Object.keys(row.values).length > 0);

    return [...deletes, ...updates, ...inserts];
  }, [edits, deleted, inserted, keyColumns, originalValue]);

  return {
    changes,
    isDirty: changes.length > 0,
    insertedRows: inserted.length,
    edit, insertRow, deleteRow, duplicateRow, revert, revertAll, cellState, editedValue,
  };
}

function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || a === undefined) return b === null || b === undefined || b === "";
  return String(a) === String(b);
}
