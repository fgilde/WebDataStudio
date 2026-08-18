import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Pagination, Text, Tooltip,
} from "@mantine/core";
import {
  IconArrowRight, IconCopyPlus, IconDeviceFloppy, IconPlus, IconRestore, IconTrash, IconWand,
} from "@tabler/icons-react";
import { browseData, type DataPageDto, type ForeignKeyDto } from "../api";
import { CellValue } from "../grid/CellValue";
import { EditableCell } from "../grid/editing/EditableCell";
import { ChangePreviewModal } from "../grid/editing/ChangePreviewModal";
import { BulkUpdateModal } from "../grid/editing/BulkUpdateModal";
import { useChangeSet, type RowChange } from "../grid/editing/useChangeSet";

const PAGE_SIZE = 200;

export interface DataTabProps {
  connectionId: string;
  objectRef: string;
  tableName: string;
  foreignKeys?: ForeignKeyDto[];
  onFollowForeignKey?: (fk: ForeignKeyDto, value: unknown) => void;
}

export function DataTab({ connectionId, objectRef, tableName, foreignKeys = [], onFollowForeignKey }: DataTabProps) {
  const [page, setPage] = useState<DataPageDto | null>(null);
  const [pageIndex, setPageIndex] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<RowChange[] | null>(null);
  const [bulk, setBulk] = useState<{ rowIndex: number; column: string; value: unknown }[] | null>(null);
  const [selected, setSelected] = useState<{ row: number; col: number }[]>([]);
  const [nonce, setNonce] = useState(0);

  const columns = useMemo(() => page?.columns.map(c => c.name) ?? [], [page]);
  const rowAt = useCallback((index: number) => page?.rows[index], [page]);
  const changeSet = useChangeSet(page?.keyColumns ?? [], columns, rowAt);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    browseData(connectionId, objectRef, { offset: (pageIndex - 1) * PAGE_SIZE, limit: PAGE_SIZE })
      .then(p => { if (!cancelled) setPage(p); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [connectionId, objectRef, pageIndex, nonce]);

  if (error) return <Text c="red" size="xs" p="xs">{error}</Text>;
  if (!page) return <Loader size="xs" m="xs" />;

  const fkForColumn = (column: string) => foreignKeys.find(fk => fk.columns.includes(column));
  const isBoolean = (type: string) => /bool|bit/i.test(type);

  const insertedRows = Array.from({ length: changeSet.insertedRows }, (_, i) => -(i + 1));
  const totalPages = page.totalEstimate ? Math.max(1, Math.ceil(page.totalEstimate / PAGE_SIZE)) : 1;

  const selectedCells = selected
    .map(s => ({ rowIndex: s.row, column: columns[s.col], value: changeSet.editedValue(s.row, columns[s.col]) }))
    .filter(c => c.column !== undefined);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={4} wrap="nowrap">
        <Tooltip label={page.editable ? "Save changes" : page.reason ?? "not editable"}>
          <Button size="compact-xs" leftSection={<IconDeviceFloppy size={13} />}
            disabled={!page.editable || !changeSet.isDirty}
            onClick={() => setPending(changeSet.changes)}>
            Save
          </Button>
        </Tooltip>
        <Tooltip label="Revert all pending changes">
          <ActionIcon size="sm" variant="subtle" aria-label="Revert" disabled={!changeSet.isDirty}
            onClick={changeSet.revertAll}><IconRestore size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Insert row">
          <ActionIcon size="sm" variant="subtle" aria-label="Insert row" disabled={!page.editable}
            onClick={() => changeSet.insertRow({})}><IconPlus size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Duplicate selected row">
          <ActionIcon size="sm" variant="subtle" aria-label="Duplicate row"
            disabled={!page.editable || selected.length === 0}
            onClick={() => {
              const row = page.rows[selected[0].row];
              if (row) changeSet.duplicateRow(selected[0].row,
                Object.fromEntries(columns.map((c, i) => [c, row[i]])));
            }}><IconCopyPlus size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Delete selected row">
          <ActionIcon size="sm" variant="subtle" color="red" aria-label="Delete row"
            disabled={!page.editable || selected.length === 0}
            onClick={() => changeSet.deleteRow(selected[0].row)}><IconTrash size={14} /></ActionIcon>
        </Tooltip>
        <Tooltip label="Bulk update the selection">
          <ActionIcon size="sm" variant="subtle" aria-label="Bulk update"
            disabled={!page.editable || selectedCells.length === 0}
            onClick={() => setBulk(selectedCells)}><IconWand size={14} /></ActionIcon>
        </Tooltip>

        <Text size="xs" c="dimmed" ml="auto">
          {page.rows.length} rows
          {page.totalEstimate ? ` of ~${page.totalEstimate}` : ""}
          {changeSet.isDirty && ` · ${changeSet.changes.length} pending`}
        </Text>
      </Group>

      {!page.editable && page.reason && (
        <Alert color="gray" p={6} mx={4} mb={4}>
          <Text size="xs">{page.reason}</Text>
        </Alert>
      )}

      <div style={{ flex: 1, overflow: "auto", minHeight: 0 }}>
        <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
          <thead style={{ position: "sticky", top: 0, zIndex: 1, background: "var(--mantine-color-default)" }}>
            <tr>
              {page.columns.map(c => (
                <th key={c.name} style={{
                  textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap",
                  borderBottom: "1px solid var(--mantine-color-default-border)",
                }}>
                  <Group gap={3} wrap="nowrap">
                    <Text size="xs" fw={600}>{c.name}</Text>
                    {page.keyColumns.includes(c.name) && <Badge size="xs" variant="light">key</Badge>}
                    {fkForColumn(c.name) && <IconArrowRight size={11} />}
                  </Group>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {page.rows.map((row, rowIndex) => (
              <tr key={rowIndex}>
                {page.columns.map((c, colIndex) => {
                  const fk = fkForColumn(c.name);
                  const isSelected = selected.some(s => s.row === rowIndex && s.col === colIndex);
                  return (
                    <td key={c.name}
                      onMouseDown={e => setSelected(prev => e.ctrlKey || e.metaKey
                        ? [...prev, { row: rowIndex, col: colIndex }]
                        : [{ row: rowIndex, col: colIndex }])}
                      style={{
                        padding: "1px 8px", whiteSpace: "nowrap",
                        borderBottom: "1px solid var(--mantine-color-default-border)",
                        background: isSelected ? "var(--mantine-primary-color-light)" : undefined,
                      }}>
                      <Group gap={2} wrap="nowrap" style={{ width: "100%" }}>
                        <EditableCell
                          value={changeSet.editedValue(rowIndex, c.name)}
                          state={changeSet.cellState(rowIndex, c.name)}
                          editable={page.editable}
                          boolean={isBoolean(c.dataType)}
                          onCommit={value => changeSet.edit(rowIndex, c.name, value)} />
                        {fk && onFollowForeignKey && (
                          <Tooltip label={`Go to ${fk.referencedTable}`}>
                            <ActionIcon size="xs" variant="subtle" aria-label="Follow foreign key"
                              onClick={() => onFollowForeignKey(fk, row[colIndex])}>
                              <IconArrowRight size={11} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Group>
                    </td>
                  );
                })}
              </tr>
            ))}

            {insertedRows.map(index => (
              <tr key={index} style={{ background: "color-mix(in srgb, var(--mantine-color-green-6) 8%, transparent)" }}>
                {page.columns.map(c => (
                  <td key={c.name} style={{ padding: "1px 8px", whiteSpace: "nowrap" }}>
                    <EditableCell
                      value={changeSet.editedValue(index, c.name)}
                      state="inserted"
                      editable
                      boolean={isBoolean(c.dataType)}
                      onCommit={value => changeSet.edit(index, c.name, value)} />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {totalPages > 1 && (
        <Group justify="center" py={4}>
          <Pagination size="xs" total={totalPages} value={pageIndex} onChange={setPageIndex} />
        </Group>
      )}

      <ChangePreviewModal
        connectionId={connectionId} objectRef={objectRef} tableName={tableName}
        changes={pending}
        onClose={() => setPending(null)}
        onApplied={() => { changeSet.revertAll(); setNonce(n => n + 1); }} />

      <BulkUpdateModal values={bulk} onClose={() => setBulk(null)}
        onApply={transformed => transformed.forEach(t => changeSet.edit(t.rowIndex, t.column, t.value))} />
    </div>
  );
}

/// Rendered when a cell has no editor: keeps the read-only path visually identical.
export const ReadOnlyCell = CellValue;
