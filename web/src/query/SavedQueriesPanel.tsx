import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Alert, Button, Group, Menu, Modal, ScrollArea, Stack, Text, TextInput, Tooltip,
} from "@mantine/core";
import {
  IconChevronDown, IconChevronRight, IconCopy, IconDots, IconFolder, IconPlus, IconTrash,
} from "@tabler/icons-react";
import {
  createSavedQuery, deleteSavedQuery, listSavedQueries, updateSavedQuery, type SavedQueryDto,
} from "../api";

export function SavedQueriesPanel({ onOpen, currentSql, currentConnectionId }: {
  onOpen: (query: SavedQueryDto) => void;
  currentSql?: string;
  currentConnectionId?: string;
}) {
  const [queries, setQueries] = useState<SavedQueryDto[]>([]);
  const [search, setSearch] = useState("");
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<SavedQueryDto | null>(null);
  const [saving, setSaving] = useState(false);

  const reload = useCallback(() => {
    listSavedQueries().then(setQueries).catch(e => setError(e.message));
  }, []);

  useEffect(reload, [reload]);

  const folders = useMemo(() => {
    const matching = queries.filter(q =>
      search.trim() === "" ||
      q.name.toLowerCase().includes(search.toLowerCase()) ||
      q.sql.toLowerCase().includes(search.toLowerCase()));

    const grouped = new Map<string, SavedQueryDto[]>();
    for (const query of matching) {
      const folder = query.folder ?? "";
      if (!grouped.has(folder)) grouped.set(folder, []);
      grouped.get(folder)!.push(query);
    }
    return [...grouped.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [queries, search]);

  const toggle = (folder: string) => {
    const next = new Set(collapsed);
    if (next.has(folder)) next.delete(folder); else next.add(folder);
    setCollapsed(next);
  };

  const save = async () => {
    if (!editing) return;
    setSaving(true);
    try {
      if (editing.id) await updateSavedQuery(editing.id, editing);
      else await createSavedQuery(editing);
      setEditing(null);
      reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={4}>
        <TextInput size="xs" flex={1} placeholder="Search saved queries" value={search}
          onChange={e => setSearch(e.currentTarget.value)} />
        <Tooltip label="Save the current query">
          <ActionIcon size="sm" variant="subtle" aria-label="Save current query"
            disabled={!currentSql?.trim()}
            onClick={() => setEditing({
              id: "", name: "", folder: null, sql: currentSql ?? "",
              connectionId: currentConnectionId ?? null, updatedAt: "",
            })}>
            <IconPlus size={15} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {error ? <Alert color="red" variant="light" m={4} onClose={() => setError(null)} withCloseButton>
        {error}
      </Alert> : null}

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        {folders.length === 0
          ? <Text size="xs" c="dimmed" p="xs">Nothing saved yet.</Text>
          : folders.map(([folder, list]) => (
            <div key={folder}>
              {folder ? (
                <Group gap={4} px={6} py={2} style={{ cursor: "pointer" }} onClick={() => toggle(folder)}>
                  {collapsed.has(folder) ? <IconChevronRight size={13} /> : <IconChevronDown size={13} />}
                  <IconFolder size={13} />
                  <Text size="xs" fw={600}>{folder}</Text>
                </Group>
              ) : null}

              {!collapsed.has(folder) && list.map(query => (
                <Group key={query.id} gap={4} pl={folder ? 26 : 8} pr={4} py={1} wrap="nowrap"
                  style={{ cursor: "pointer" }} onDoubleClick={() => onOpen(query)}>
                  <Text size="xs" flex={1} truncate>{query.name}</Text>
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
                        onClick={() => createSavedQuery({
                          ...query, name: `${query.name} copy`,
                        }).then(reload).catch(e => setError(e.message))}>
                        Duplicate
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item color="red" leftSection={<IconTrash size={13} />}
                        onClick={() => deleteSavedQuery(query.id).then(reload).catch(e => setError(e.message))}>
                        Delete
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                </Group>
              ))}
            </div>
          ))}
      </ScrollArea>

      <Modal opened={editing !== null} onClose={() => setEditing(null)}
        title={editing?.id ? "Edit saved query" : "Save query"}>
        <Stack gap="xs">
          <TextInput size="xs" label="Name" data-autofocus value={editing?.name ?? ""}
            onChange={e => setEditing(q => q && { ...q, name: e.currentTarget.value })} />
          {/* A folder is a plain name, not a tree: one level keeps the panel readable. */}
          <TextInput size="xs" label="Folder" placeholder="optional" value={editing?.folder ?? ""}
            onChange={e => setEditing(q => q && { ...q, folder: e.currentTarget.value })} />
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setEditing(null)}>Cancel</Button>
            <Button size="xs" loading={saving} disabled={!editing?.name.trim()} onClick={save}>Save</Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
