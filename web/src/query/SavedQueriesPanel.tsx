import { useCallback, useEffect, useMemo, useState, type DragEvent, type ReactNode } from "react";
import {
  ActionIcon, Alert, Button, Group, Menu, Modal, ScrollArea, Stack, Text, TextInput, Tooltip,
} from "@mantine/core";
import {
  IconChevronDown, IconChevronRight, IconCopy, IconDeviceFloppy, IconDots, IconFolder, IconFolderPlus,
  IconPencil, IconTrash,
} from "@tabler/icons-react";
import {
  createSavedQuery, deleteSavedQuery, listSavedQueries, loadWorkspaceItem, saveWorkspaceItem,
  updateSavedQuery, type SavedQueryDto,
} from "../api";
import { onShell } from "../shell/bus";
import { SaveQueryDialog } from "./SaveQueryDialog";
import {
  allFolders, EMPTY_FOLDERS_KEY, isWithin, leafOf, moveFolder, normalizeFolder, parentOf,
} from "./savedFolders";

const QUERY_TYPE = "application/x-wds-saved-query";
const FOLDER_TYPE = "application/x-wds-saved-folder";

export function SavedQueriesPanel({ onOpen, onSaveCurrent, canSaveCurrent }: {
  onOpen: (query: SavedQueryDto) => void;
  /// The shell's Save: it knows which tab is current and whether that tab already is a saved query.
  onSaveCurrent: () => void;
  canSaveCurrent: boolean;
}) {
  const [queries, setQueries] = useState<SavedQueryDto[]>([]);
  const [emptyFolders, setEmptyFolders] = useState<string[]>([]);
  const [search, setSearch] = useState("");
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<SavedQueryDto | null>(null);
  // A folder being made (under `parent`) or renamed (`path`).
  const [folderPrompt, setFolderPrompt] =
    useState<{ parent: string | null; path?: string; name: string } | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null | undefined>(undefined);

  const fail = (e: unknown) => setError(e instanceof Error ? e.message : String(e));

  const reload = useCallback(() => {
    listSavedQueries().then(setQueries).catch(fail);
    loadWorkspaceItem<string[]>(EMPTY_FOLDERS_KEY).then(list => setEmptyFolders(list ?? [])).catch(() => {});
  }, []);

  useEffect(reload, [reload]);
  useEffect(() => onShell("saved-queries-changed", reload), [reload]);

  const folders = useMemo(
    () => allFolders([...queries.map(q => q.folder), ...emptyFolders]), [queries, emptyFolders]);

  const storeEmpty = (list: string[]) => {
    const next = allFolders(list);
    setEmptyFolders(next);
    return saveWorkspaceItem(EMPTY_FOLDERS_KEY, next).catch(fail);
  };

  const toggle = (folder: string) => setCollapsed(current => {
    const next = new Set(current);
    if (!next.delete(folder)) next.add(folder);
    return next;
  });

  const moveQuery = (query: SavedQueryDto, folder: string | null) =>
    updateSavedQuery(query.id, { ...query, folder }).then(reload).catch(fail);

  // ponytail: one PUT per query inside the folder; a batch endpoint once folders hold hundreds.
  const relocateFolder = async (from: string, to: string | null) => {
    if (to !== null && isWithin(to, from)) return;
    try {
      await Promise.all(queries.filter(q => isWithin(q.folder, from))
        .map(q => updateSavedQuery(q.id, { ...q, folder: moveFolder(q.folder, from, to) })));
      await storeEmpty([...emptyFolders.map(f => moveFolder(f, from, to)), to]
        .filter((f): f is string => f !== null));
    } catch (e) { fail(e); }
    reload();
  };

  const submitFolder = async () => {
    if (!folderPrompt) return;
    const path = normalizeFolder(`${folderPrompt.parent ?? ""}/${folderPrompt.name.replaceAll("/", " ")}`);
    setFolderPrompt(null);
    if (!path) return;

    if (folderPrompt.path) await relocateFolder(folderPrompt.path, path);
    else await storeEmpty([...emptyFolders, path]);
  };

  const removeFolder = (path: string) => storeEmpty(emptyFolders.filter(f => !isWithin(f, path)));

  // Drag a query or a folder onto a folder, or onto the empty space for the top level.
  const dropProps = (folder: string | null) => ({
    onDragOver: (e: DragEvent) => {
      if (!e.dataTransfer.types.includes(QUERY_TYPE) && !e.dataTransfer.types.includes(FOLDER_TYPE)) return;
      e.preventDefault();
      e.stopPropagation();
      setDropTarget(folder);
    },
    onDragLeave: () => setDropTarget(undefined),
    onDrop: (e: DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setDropTarget(undefined);
      const id = e.dataTransfer.getData(QUERY_TYPE);
      const path = e.dataTransfer.getData(FOLDER_TYPE);
      const query = queries.find(q => q.id === id);
      if (query && query.folder !== folder) void moveQuery(query, folder);
      if (path && parentOf(path) !== folder) void relocateFolder(path, normalizeFolder(`${folder ?? ""}/${leafOf(path)}`));
    },
  });

  const highlight = (folder: string | null) =>
    dropTarget === folder ? { background: "var(--mantine-color-blue-light)" } : undefined;

  const queryRow = (query: SavedQueryDto, indent: number, hint?: string) => (
    <Group key={query.id} gap={4} pl={8 + indent * 14} pr={4} py={1} wrap="nowrap"
      style={{ cursor: "pointer" }} onDoubleClick={() => onOpen(query)}
      draggable onDragStart={e => e.dataTransfer.setData(QUERY_TYPE, query.id)}>
      <Text size="xs" flex={1} truncate>{query.name}</Text>
      {hint && <Text size="xs" c="dimmed" truncate maw="45%">{hint}</Text>}
      <Menu withinPortal position="bottom-end">
        <Menu.Target>
          <ActionIcon size="xs" variant="subtle" aria-label={`Actions for ${query.name}`}>
            <IconDots size={13} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item onClick={() => onOpen(query)}>Open in a new tab</Menu.Item>
          <Menu.Item onClick={() => setEditing(query)}>Rename or move</Menu.Item>
          <Menu.Item leftSection={<IconCopy size={13} />}
            onClick={() => createSavedQuery({ ...query, name: `${query.name} copy` }).then(reload).catch(fail)}>
            Duplicate
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item color="red" leftSection={<IconTrash size={13} />}
            onClick={() => deleteSavedQuery(query.id).then(reload).catch(fail)}>
            Delete
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
    </Group>
  );

  const folderNode = (path: string): ReactNode => {
    const depth = path.split("/").length - 1;
    const used = queries.some(q => isWithin(q.folder, path));

    return (
      <div key={path}>
        <Group gap={4} pl={6 + depth * 14} pr={4} py={2} wrap="nowrap" style={{ cursor: "pointer", ...highlight(path) }}
          onClick={() => toggle(path)} draggable
          onDragStart={e => { e.stopPropagation(); e.dataTransfer.setData(FOLDER_TYPE, path); }}
          {...dropProps(path)}>
          {collapsed.has(path) ? <IconChevronRight size={13} /> : <IconChevronDown size={13} />}
          <IconFolder size={13} />
          <Text size="xs" fw={600} flex={1} truncate>{leafOf(path)}</Text>
          <Menu withinPortal position="bottom-end">
            <Menu.Target>
              <ActionIcon size="xs" variant="subtle" aria-label={`Actions for folder ${path}`}
                onClick={e => e.stopPropagation()}>
                <IconDots size={13} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown onClick={e => e.stopPropagation()}>
              <Menu.Item leftSection={<IconFolderPlus size={13} />}
                onClick={() => setFolderPrompt({ parent: path, name: "" })}>New folder inside</Menu.Item>
              <Menu.Item leftSection={<IconPencil size={13} />}
                onClick={() => setFolderPrompt({ parent: parentOf(path), path, name: leafOf(path) })}>Rename</Menu.Item>
              <Menu.Divider />
              {/* Deleting a folder never deletes queries: empty it first. */}
              <Menu.Item color="red" leftSection={<IconTrash size={13} />} disabled={used}
                onClick={() => void removeFolder(path)}>
                {used ? "Delete (only when empty)" : "Delete"}
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>

        {!collapsed.has(path) && (
          <>
            {folders.filter(f => parentOf(f) === path).map(folderNode)}
            {queries.filter(q => normalizeFolder(q.folder) === path)
              .sort((a, b) => a.name.localeCompare(b.name)).map(q => queryRow(q, depth + 1))}
          </>
        )}
      </div>
    );
  };

  const needle = search.trim().toLowerCase();
  const matches = needle
    ? queries.filter(q => q.name.toLowerCase().includes(needle) || q.sql.toLowerCase().includes(needle))
    : [];

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Search saved queries" value={search}
          onChange={e => setSearch(e.currentTarget.value)} />
        <Tooltip label="New folder">
          <ActionIcon size="sm" variant="subtle" aria-label="New folder"
            onClick={() => setFolderPrompt({ parent: null, name: "" })}>
            <IconFolderPlus size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Save the current query (Ctrl+S)">
          <ActionIcon size="sm" variant="subtle" aria-label="Save current query"
            disabled={!canSaveCurrent} onClick={onSaveCurrent}>
            <IconDeviceFloppy size={15} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {error ? <Alert color="red" variant="light" m={4} onClose={() => setError(null)} withCloseButton>
        {error}
      </Alert> : null}

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <div style={{ minHeight: "100%", paddingBottom: 24, ...highlight(null) }} {...dropProps(null)}>
          {needle
            ? matches.length === 0
              ? <Text size="xs" c="dimmed" p="xs">Nothing matches.</Text>
              : matches.map(q => queryRow(q, 0, q.folder ?? undefined))
            : folders.length === 0 && queries.length === 0
              ? <Text size="xs" c="dimmed" p="xs">Nothing saved yet.</Text>
              : <>
                  {folders.filter(f => parentOf(f) === null).map(folderNode)}
                  {queries.filter(q => normalizeFolder(q.folder) === null)
                    .sort((a, b) => a.name.localeCompare(b.name)).map(q => queryRow(q, 0))}
                </>}
        </div>
      </ScrollArea>

      <SaveQueryDialog draft={editing} onClose={() => setEditing(null)}
        onSaved={() => { setEditing(null); reload(); }} />

      <Modal opened={folderPrompt !== null} onClose={() => setFolderPrompt(null)}
        title={folderPrompt?.path ? `Rename ${folderPrompt.path}`
          : folderPrompt?.parent ? `New folder in ${folderPrompt.parent}` : "New folder"}>
        <Stack gap="xs">
          <TextInput size="xs" label="Name" data-autofocus value={folderPrompt?.name ?? ""}
            onChange={e => { const name = e.currentTarget.value; setFolderPrompt(p => p && { ...p, name }); }}
            onKeyDown={e => { if (e.key === "Enter") void submitFolder(); }} />
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setFolderPrompt(null)}>Cancel</Button>
            <Button size="xs" disabled={!folderPrompt?.name.trim()} onClick={submitFolder}>
              {folderPrompt?.path ? "Rename" : "Create"}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
