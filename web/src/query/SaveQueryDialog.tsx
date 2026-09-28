import { useEffect, useState } from "react";
import { ActionIcon, Button, Group, Modal, Select, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { IconFolder, IconFolderPlus } from "@tabler/icons-react";
import {
  createSavedQuery, listSavedQueries, loadWorkspaceItem, updateSavedQuery, type SavedQueryDto,
} from "../api";
import { allFolders, depthOf, EMPTY_FOLDERS_KEY, leafOf, normalizeFolder } from "./savedFolders";

/// Save, save as, rename and move are one dialog: a name and a folder picked from the ones there
/// are — a new one can be made right there, under the folder that is picked.
export function SaveQueryDialog({ draft, onClose, onSaved }: {
  /// Null is closed. An empty id saves a new query; any other id overwrites that one.
  draft: SavedQueryDto | null;
  onClose: () => void;
  onSaved: (saved: SavedQueryDto) => void;
}) {
  const [name, setName] = useState("");
  const [folder, setFolder] = useState<string | null>(null);
  const [folders, setFolders] = useState<string[]>([]);
  const [newFolder, setNewFolder] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!draft) return;
    setName(draft.name);
    setFolder(normalizeFolder(draft.folder));
    setNewFolder(null);
    setError(null);

    Promise.all([
      listSavedQueries().catch(() => []),
      loadWorkspaceItem<string[]>(EMPTY_FOLDERS_KEY).catch(() => null),
    ]).then(([queries, empty]) =>
      setFolders(allFolders([...queries.map(q => q.folder), ...(empty ?? []), draft.folder])));
  }, [draft]);

  const addFolder = () => {
    const path = normalizeFolder(`${folder ?? ""}/${newFolder ?? ""}`);
    if (path) {
      setFolders(list => allFolders([...list, path]));
      setFolder(path);
    }
    setNewFolder(null);
  };

  const save = async () => {
    if (!draft || !name.trim()) return;
    setSaving(true);
    try {
      const body = { ...draft, name: name.trim(), folder };
      onSaved(draft.id ? await updateSavedQuery(draft.id, body) : await createSavedQuery(body));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened={draft !== null} onClose={onClose} title={draft?.id ? "Edit saved query" : "Save query"}>
      <Stack gap="xs">
        <TextInput size="xs" label="Name" data-autofocus value={name}
          onChange={e => setName(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === "Enter") void save(); }} />

        <Group gap={4} align="flex-end" wrap="nowrap">
          <Select size="xs" flex={1} label="Folder" placeholder="Top level" clearable searchable
            data={folders} value={folder} onChange={setFolder} nothingFoundMessage="No such folder"
            leftSection={<IconFolder size={14} />}
            // The whole path is what is searched and shown once picked; the list reads as a tree.
            renderOption={({ option }) => (
              <Text size="xs" pl={depthOf(option.value) * 12}>{leafOf(option.value)}</Text>
            )} />
          <Tooltip label={folder ? `New folder in ${folder}` : "New folder"}>
            <ActionIcon size="lg" variant="default" aria-label="New folder" onClick={() => setNewFolder("")}>
              <IconFolderPlus size={15} />
            </ActionIcon>
          </Tooltip>
        </Group>

        {newFolder !== null && (
          <TextInput size="xs" autoFocus placeholder={folder ? `in ${folder}/…` : "folder name"}
            description="Enter to add, Escape to drop it" value={newFolder}
            onChange={e => setNewFolder(e.currentTarget.value)}
            onKeyDown={e => {
              if (e.key === "Enter") { e.preventDefault(); addFolder(); }
              if (e.key === "Escape") { e.stopPropagation(); setNewFolder(null); }
            }} />
        )}

        {error && <Text size="xs" c="red">{error}</Text>}

        <Group justify="flex-end">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" loading={saving} disabled={!name.trim()} onClick={save}>Save</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
