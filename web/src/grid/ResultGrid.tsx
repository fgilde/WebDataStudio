import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Group, Menu, Text, TextInput } from "@mantine/core";
import {
  IconEye, IconEyeOff, IconFilter, IconSortAscending, IconSortDescending,
} from "@tabler/icons-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { StatementResult } from "../query/resultStore";
import { CellValue } from "./CellValue";
import { CellViewerModal, type CellRef } from "./CellViewerModal";
import { summarizeSelection } from "./aggregate";
import { GroupedTable } from "./GroupedTable";
import { MenuFilterInput } from "./MenuFilterInput";
import { loadWorkspaceItem, saveWorkspaceItem } from "../api";

const WIDTH_KEY = "grid-column-widths";

const ROW_HEIGHT = 24;

/// Starting width per column. Fixed rather than content-derived, because a table has to agree on
/// its columns before the rows are measured; the header's resize handle overrides it.
const DEFAULT_WIDTH = 160;

export function ResultGrid({ result, onSelectionChange }: {
  result: StatementResult;
  onSelectionChange?: (values: unknown[]) => void;
}) {
  const parentRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<{ index: number; desc: boolean } | null>(null);
  const [filters, setFilters] = useState<Record<number, string>>({});
  const [search, setSearch] = useState("");
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [selected, setSelected] = useState<{ row: number; col: number }[]>([]);
  const [viewing, setViewing] = useState<CellRef | null>(null);
  const [groupBy, setGroupBy] = useState<number | null>(null);
  const [order, setOrderIndexes] = useState<number[] | null>(null);
  const [pinned, setPinned] = useState<Set<number>>(new Set());
  const [widths, setWidths] = useState<Record<string, number>>({});
  const dragging = useRef<{ name: string; startX: number; startWidth: number } | null>(null);

  // Widths are remembered by column name, so the same report keeps its layout across runs.
  useEffect(() => {
    loadWorkspaceItem<Record<string, number>>(WIDTH_KEY)
      .then(stored => setWidths(stored && typeof stored === "object" ? stored : {}))
      .catch(() => setWidths({}));
  }, []);

  useEffect(() => {
    const move = (event: MouseEvent) => {
      if (!dragging.current) return;
      const next = Math.max(60, dragging.current.startWidth + event.clientX - dragging.current.startX);
      setWidths(w => ({ ...w, [dragging.current!.name]: next }));
    };

    const up = () => {
      if (!dragging.current) return;
      dragging.current = null;
      // One write when the drag ends, not one per mouse move.
      setWidths(w => { void saveWorkspaceItem(WIDTH_KEY, w).catch(() => {}); return w; });
    };

    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
    return () => { window.removeEventListener("mousemove", move); window.removeEventListener("mouseup", up); };
  }, []);

  const visibleColumns = (() => {
    const all = result.columns.map((c, index) => ({ ...c, index })).filter(c => !hidden.has(c.index));
    const sequence = order ?? all.map(c => c.index);

    const ordered = sequence
      .map(index => all.find(c => c.index === index))
      .filter((c): c is typeof all[number] => c !== undefined);

    // Anything the order does not mention yet — a fresh result with more columns — keeps its place.
    for (const column of all) if (!ordered.includes(column)) ordered.push(column);

    // Pinned columns move to the front and stick there while the rest scrolls.
    return [...ordered.filter(c => pinned.has(c.index)), ...ordered.filter(c => !pinned.has(c.index))];
  })();

  const move = (index: number, delta: number) => {
    const sequence = (order ?? visibleColumns.map(c => c.index)).slice();
    const at = sequence.indexOf(index);
    const to = at + delta;
    if (at < 0 || to < 0 || to >= sequence.length) return;

    [sequence[at], sequence[to]] = [sequence[to], sequence[at]];
    setOrderIndexes(sequence);
  };

  // Sorting and filtering happen client-side over the rows already fetched. Anything beyond the
  // fetch cap needs a server round trip, which the result footer's "load more" will trigger.
  const rows = useMemo(() => {
    let out = result.rows;

    const active = Object.entries(filters).filter(([, v]) => v.trim() !== "");
    if (active.length > 0)
      out = out.filter(row => active.every(([i, v]) =>
        String(row[Number(i)] ?? "").toLowerCase().includes(v.toLowerCase())));

    if (search.trim() !== "")
      out = out.filter(row => row.some(cell =>
        String(cell ?? "").toLowerCase().includes(search.toLowerCase())));

    if (sort) {
      const { index, desc } = sort;
      out = out.slice().sort((a, b) => {
        const x = a[index];
        const y = b[index];
        if (x === null || x === undefined) return desc ? 1 : -1;
        if (y === null || y === undefined) return desc ? -1 : 1;
        const nx = Number(x);
        const ny = Number(y);
        const cmp = Number.isFinite(nx) && Number.isFinite(ny)
          ? nx - ny
          : String(x).localeCompare(String(y));
        return desc ? -cmp : cmp;
      });
    }
    return out;
  }, [result.rows, filters, search, sort]);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });

  const virtualItems = virtualizer.getVirtualItems();
  const total = virtualizer.getTotalSize();
  const paddingTop = virtualItems.length > 0 ? virtualItems[0].start : 0;
  const paddingBottom = virtualItems.length > 0 ? total - virtualItems[virtualItems.length - 1].end : 0;

  const selectedValues = selected.map(s => rows[s.row]?.[s.col]);
  const summary = summarizeSelection(selectedValues);

  useEffect(() => { onSelectionChange?.(selectedValues); },
    // The array identity changes every render; its contents are what matter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [JSON.stringify(selectedValues), onSelectionChange]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Search in result"
          value={search} onChange={e => setSearch(e.currentTarget.value)} />
        {/* Hidden columns are otherwise invisible by definition: this is the way back to them. */}
        {hidden.size > 0 ? (
          <Menu withinPortal closeOnItemClick={false} position="bottom-end">
            <Menu.Target>
              <Button size="compact-xs" variant="subtle" color="gray"
                aria-label={`${hidden.size} hidden columns`}
                leftSection={<IconEyeOff size={13} />}>{hidden.size}</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Hidden columns</Menu.Label>
              {[...hidden].map(index => (
                <Menu.Item key={index} leftSection={<IconEye size={13} />}
                  onClick={() => setHidden(h => {
                    const next = new Set(h);
                    next.delete(index);
                    return next;
                  })}>{result.columns[index]?.name ?? `#${index}`}</Menu.Item>
              ))}
              <Menu.Divider />
              <Menu.Item onClick={() => setHidden(new Set())}>Show all columns</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        ) : null}
        <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>
          {rows.length === result.rows.length
            ? `${rows.length} rows`
            : `${rows.length} of ${result.rows.length} rows`}
          {result.truncated && " · capped"}
          {result.elapsedMs !== null && ` · ${result.elapsedMs} ms`}
        </Text>
      </Group>

      {groupBy !== null ? (
        <div style={{ flex: 1, minHeight: 0 }}>
          <GroupedTable columns={visibleColumns} rows={rows} groupBy={groupBy} />
        </div>
      ) : (
      <div ref={parentRef} style={{ flex: 1, overflow: "auto", minHeight: 0 }}>
        <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
          {/* One column model for head and body. Without it a resized header and its cells drift
              apart, and a wide result collapses because each part sizes itself. */}
          <colgroup>
            {visibleColumns.map(c => (
              <col key={c.index} style={{ width: widths[c.name] ?? DEFAULT_WIDTH }} />
            ))}
          </colgroup>
          <thead style={{ position: "sticky", top: 0, zIndex: 1, background: "var(--mantine-color-default)" }}>
            <tr>
              {visibleColumns.map(c => (
                <th key={c.index} style={{
                  textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap", position: "relative",
                  overflow: "hidden",
                  borderBottom: "1px solid var(--mantine-color-default-border)",
                }}>
                  <Menu withinPortal closeOnItemClick={false}>
                    <Menu.Target>
                      <Group gap={2} style={{ cursor: "pointer" }} wrap="nowrap">
                        <Text size="xs" fw={600}>{c.name}</Text>
                        {sort?.index === c.index && (sort.desc
                          ? <IconSortDescending size={12} /> : <IconSortAscending size={12} />)}
                        {filters[c.index] && <IconFilter size={12} />}
                      </Group>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: false })}>Sort ascending</Menu.Item>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: true })}>Sort descending</Menu.Item>
                      <Menu.Item onClick={() => setSort(null)}>Clear sort</Menu.Item>
                      <Menu.Divider />
                      {/* Shared with the data tab, which had the same bug: an input inside a
                          Menu.Item never takes the focus. This grid filters in memory, so no
                          debounce. */}
                      <MenuFilterInput value={filters[c.index] ?? ""}
                        onChange={value => setFilters(f => ({ ...f, [c.index]: value }))} />
                      <Menu.Divider />
                      <Menu.Item onClick={() => setPinned(p => {
                        const next = new Set(p);
                        if (next.has(c.index)) next.delete(c.index); else next.add(c.index);
                        return next;
                      })}>{pinned.has(c.index) ? "Unpin column" : "Pin column"}</Menu.Item>
                      <Menu.Item onClick={() => move(c.index, -1)}>Move left</Menu.Item>
                      <Menu.Item onClick={() => move(c.index, 1)}>Move right</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item onClick={() => setGroupBy(c.index)}>Group by this column</Menu.Item>
                      <Menu.Item disabled={groupBy === null}
                        onClick={() => setGroupBy(null)}>Clear grouping</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item onClick={() => setHidden(h => new Set(h).add(c.index))}>Hide column</Menu.Item>
                      <Menu.Item onClick={() => setHidden(new Set())}>Show all columns</Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>

                  {/* A grab strip on the right edge; the drag itself lives on the window. */}
                  <span
                    onMouseDown={event => {
                      dragging.current = {
                        name: c.name, startX: event.clientX,
                        startWidth: widths[c.name] ?? (event.currentTarget.parentElement?.clientWidth ?? 120),
                      };
                      event.preventDefault();
                    }}
                    style={{
                      position: "absolute", top: 0, right: 0, width: 5, height: "100%",
                      cursor: "col-resize",
                    }} />
                </th>
              ))}
            </tr>
          </thead>
          {/* Virtualised with spacer rows rather than absolute positioning: a row taken out of
              the table's layout does not line up with the header, which silently collapsed every
              result wider than a few columns. */}
          <tbody>
            {paddingTop > 0 ? <tr style={{ height: paddingTop }} aria-hidden /> : null}

            {virtualItems.map(item => (
              <tr key={item.key} style={{ height: ROW_HEIGHT }}>
                {visibleColumns.map(c => {
                  const isSelected = selected.some(s => s.row === item.index && s.col === c.index);
                  return (
                    <td key={c.index}
                      onMouseDown={e => setSelected(prev => e.ctrlKey || e.metaKey
                        ? [...prev, { row: item.index, col: c.index }]
                        : [{ row: item.index, col: c.index }])}
                      onDoubleClick={() => setViewing({
                        row: item.index, col: c.index, column: c.name, value: rows[item.index]?.[c.index],
                      })}
                      style={{
                        padding: "2px 8px", whiteSpace: "nowrap", cursor: "cell", overflow: "hidden",
                        textOverflow: "ellipsis", maxWidth: 0,
                        borderBottom: "1px solid var(--mantine-color-default-border)",
                        background: isSelected ? "var(--mantine-primary-color-light)" : undefined,
                      }}>
                      <CellValue value={rows[item.index]?.[c.index]} />
                    </td>
                  );
                })}
              </tr>
            ))}

            {paddingBottom > 0 ? <tr style={{ height: paddingBottom }} aria-hidden /> : null}
          </tbody>
        </table>
      </div>
      )}

      {selected.length > 0 && (
        <Group gap={12} px={8} py={2} style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <Text size="xs" c="dimmed">Selected: {summary.count}</Text>
          {summary.sum !== null && <Text size="xs" c="dimmed">Sum: {summary.sum}</Text>}
          {summary.avg !== null && <Text size="xs" c="dimmed">Avg: {summary.avg.toFixed(4)}</Text>}
          {summary.min !== null && <Text size="xs" c="dimmed">Min: {summary.min}</Text>}
          {summary.max !== null && <Text size="xs" c="dimmed">Max: {summary.max}</Text>}
        </Group>
      )}

      <CellViewerModal cell={viewing} onClose={() => setViewing(null)} />
    </div>
  );
}
