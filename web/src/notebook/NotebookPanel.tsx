import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon, Badge, Button, Group, Menu, Paper, ScrollArea, Select, Stack, Text, Textarea, Tooltip,
} from "@mantine/core";
import {
  IconCopy, IconDeviceFloppy, IconFilePlus, IconMessage, IconPlayerPlay, IconPlus, IconTrash,
} from "@tabler/icons-react";
import { loadWorkspaceItem, saveWorkspaceItem, type Connection } from "../api";
import { ResultArea } from "../query/ResultArea";
import { applyChunk, createResultState, type ResultState } from "../query/resultStore";
import { runQuery } from "../query/runQuery";
import {
  fromMarkdown, newCell, notebookIndexKey, notebookKey, toMarkdown, type Cell, type Notebook,
} from "./notebook";

/// SQL, prose and results in one saved document: the thing people otherwise keep in a scratch file
/// next to a chat message. It is Markdown on disk, so it can be pasted into a pull request.
export function NotebookPanel({ connections, connectionId }: {
  connections: Connection[];
  connectionId?: string;
}) {
  const [notebook, setNotebook] = useState<Notebook>(() => ({
    id: "default", name: "Notebook",
    cells: [newCell("note"), newCell("sql", connectionId)],
  }));
  const [results, setResults] = useState<Record<string, ResultState>>({});
  const [running, setRunning] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const options = connections.map(c => ({ value: c.id, label: c.name }));

  useEffect(() => {
    loadWorkspaceItem<Notebook>(notebookKey("default"))
      .then(stored => { if (stored?.cells?.length) setNotebook(stored); })
      .catch(() => {});
  }, []);

  const update = (id: string, patch: Partial<Cell>) =>
    setNotebook(n => ({ ...n, cells: n.cells.map(c => (c.id === id ? { ...c, ...patch } : c)) }));

  const save = useCallback(async (next: Notebook) => {
    try {
      await saveWorkspaceItem(notebookKey(next.id), next);
      await saveWorkspaceItem(notebookIndexKey, [{ id: next.id, name: next.name }]);
      setSaved(new Date().toLocaleTimeString());
    } catch {
      setSaved("not saved — the workspace store is unavailable");
    }
  }, []);

  const runCell = async (cell: Cell) => {
    if (cell.kind !== "sql" || !cell.text.trim()) return;

    const target = cell.connectionId ?? connectionId;
    if (!target) return;

    setRunning(cell.id);
    let state = createResultState();
    setResults(r => ({ ...r, [cell.id]: state }));

    const run = runQuery({ connectionId: target, sql: cell.text }, chunk => {
      state = applyChunk(state, chunk);
      setResults(r => ({ ...r, [cell.id]: state }));
    });

    try {
      await run.done;
    } finally {
      setRunning(null);
    }
  };

  const add = (kind: Cell["kind"], after?: string) =>
    setNotebook(n => {
      const cell = newCell(kind, connectionId);
      if (!after) return { ...n, cells: [...n.cells, cell] };
      const index = n.cells.findIndex(c => c.id === after);
      const cells = [...n.cells];
      cells.splice(index + 1, 0, cell);
      return { ...n, cells };
    });

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={4} wrap="nowrap">
        <Button size="compact-xs" variant="default" leftSection={<IconPlus size={13} />}
          onClick={() => add("sql")}>SQL cell</Button>
        <Button size="compact-xs" variant="default" leftSection={<IconMessage size={13} />}
          onClick={() => add("note")}>Note</Button>
        <Button size="compact-xs" leftSection={<IconDeviceFloppy size={13} />}
          onClick={() => save(notebook)}>Save</Button>
        <Menu withinPortal>
          <Menu.Target>
            <Button size="compact-xs" variant="subtle" leftSection={<IconCopy size={13} />}>
              Markdown
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconCopy size={13} />}
              onClick={() => navigator.clipboard.writeText(toMarkdown(notebook.cells))}>
              Copy the whole notebook
            </Menu.Item>
            <Menu.Item leftSection={<IconFilePlus size={13} />}
              onClick={async () => {
                const text = await navigator.clipboard.readText();
                const cells = fromMarkdown(text);
                if (cells.length > 0) setNotebook(n => ({ ...n, cells }));
              }}>
              Replace it from the clipboard
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
        {saved && <Text size="xs" c="dimmed" ml="auto">saved {saved}</Text>}
      </Group>

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Stack gap="xs" p="xs">
          {notebook.cells.map(cell => (
            <Paper key={cell.id} withBorder p="xs" radius="sm">
              <Group gap={4} mb={4} wrap="nowrap">
                <Badge size="xs" variant="light" color={cell.kind === "sql" ? "blue" : "gray"}>
                  {cell.kind}
                </Badge>
                {cell.kind === "sql" && (
                  <>
                    <Select size="xs" w={170} placeholder="connection" data={options} searchable
                      value={cell.connectionId ?? connectionId ?? null}
                      onChange={value => update(cell.id, { connectionId: value ?? undefined })} />
                    <Tooltip label="Run this cell (Ctrl+Enter)">
                      <ActionIcon size="sm" variant="subtle" aria-label="Run cell"
                        loading={running === cell.id} onClick={() => runCell(cell)}>
                        <IconPlayerPlay size={14} />
                      </ActionIcon>
                    </Tooltip>
                  </>
                )}
                <Tooltip label="Add a cell below">
                  <ActionIcon size="sm" variant="subtle" aria-label="Add cell below"
                    onClick={() => add(cell.kind, cell.id)}><IconPlus size={14} /></ActionIcon>
                </Tooltip>
                <Tooltip label="Remove this cell">
                  <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove cell"
                    disabled={notebook.cells.length <= 1}
                    onClick={() => setNotebook(n => ({
                      ...n, cells: n.cells.filter(c => c.id !== cell.id),
                    }))}>
                    <IconTrash size={14} />
                  </ActionIcon>
                </Tooltip>
              </Group>

              <Textarea size="xs" autosize minRows={cell.kind === "sql" ? 3 : 2} maxRows={20}
                placeholder={cell.kind === "sql" ? "SELECT …" : "What this is about"}
                styles={cell.kind === "sql" ? { input: { fontFamily: "monospace" } } : undefined}
                value={cell.text}
                onChange={e => update(cell.id, { text: e.currentTarget.value })}
                onKeyDown={e => {
                  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    void runCell({ ...cell, text: e.currentTarget.value });
                  }
                }} />

              {results[cell.id] && (
                <div style={{ height: 260, marginTop: 6 }}>
                  <ResultArea result={results[cell.id]} />
                </div>
              )}
            </Paper>
          ))}
        </Stack>
      </ScrollArea>
    </div>
  );
}
