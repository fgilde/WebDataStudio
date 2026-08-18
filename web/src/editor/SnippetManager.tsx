import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Button, Group, Modal, ScrollArea, Stack, Table, Text, TextInput, Textarea,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { loadWorkspaceItem, saveWorkspaceItem } from "../api";
import { BUILT_IN, type Snippet } from "./snippets";

const KEY = "snippets";

export function useUserSnippets(): [Snippet[], (list: Snippet[]) => Promise<void>] {
  const [snippets, setSnippets] = useState<Snippet[]>([]);

  useEffect(() => {
    loadWorkspaceItem<Snippet[]>(KEY)
      .then(list => setSnippets(Array.isArray(list) ? list : []))
      .catch(() => setSnippets([]));
  }, []);

  const save = async (list: Snippet[]) => {
    setSnippets(list);
    await saveWorkspaceItem(KEY, list);
  };

  return [snippets, save];
}

export function SnippetManager({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const [snippets, save] = useUserSnippets();
  const [draft, setDraft] = useState<Snippet | null>(null);
  const [error, setError] = useState<string | null>(null);

  const commit = async (list: Snippet[]) => {
    try { await save(list); setDraft(null); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Snippets" size="lg">
      <Stack gap="sm">
        {error ? <Alert color="red" variant="light">{error}</Alert> : null}

        <Group justify="space-between">
          <Text size="sm" fw={600}>Your snippets</Text>
          <ActionIcon size="sm" variant="subtle" aria-label="New snippet"
            onClick={() => setDraft({ prefix: "", label: "", body: "", description: "" })}>
            <IconPlus size={15} />
          </ActionIcon>
        </Group>

        <ScrollArea h={200}>
          <Table fz="xs" striped>
            <Table.Tbody>
              {snippets.length === 0
                ? <Table.Tr><Table.Td><Text size="xs" c="dimmed">None yet.</Text></Table.Td></Table.Tr>
                : snippets.map(snippet => (
                  <Table.Tr key={snippet.prefix} style={{ cursor: "pointer" }}
                    onClick={() => setDraft(snippet)}>
                    <Table.Td w={80}><Text size="xs" ff="monospace">{snippet.prefix}</Text></Table.Td>
                    <Table.Td>{snippet.label}</Table.Td>
                    <Table.Td w={40}>
                      <ActionIcon size="xs" variant="subtle" color="red"
                        aria-label={`Delete ${snippet.prefix}`}
                        onClick={e => {
                          e.stopPropagation();
                          commit(snippets.filter(s => s.prefix !== snippet.prefix));
                        }}>
                        <IconTrash size={13} />
                      </ActionIcon>
                    </Table.Td>
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>

        <Text size="sm" fw={600}>Built in</Text>
        <Text size="xs" c="dimmed">
          {BUILT_IN.map(s => s.prefix).join(", ")} — type a prefix in the editor and press Ctrl+Space.
          A snippet of yours with the same prefix replaces the built-in one.
        </Text>

        {draft ? (
          <Stack gap="xs">
            <Group grow>
              <TextInput size="xs" label="Prefix" value={draft.prefix}
                onChange={e => setDraft({ ...draft, prefix: e.currentTarget.value })} />
              <TextInput size="xs" label="Label" value={draft.label}
                onChange={e => setDraft({ ...draft, label: e.currentTarget.value })} />
            </Group>
            <TextInput size="xs" label="Description" value={draft.description}
              onChange={e => setDraft({ ...draft, description: e.currentTarget.value })} />
            {/* ${1:name} marks a tab stop, the same syntax the built-ins use. */}
            <Textarea size="xs" label="Body" autosize minRows={4} value={draft.body}
              description="Use ${1:placeholder} for tab stops"
              onChange={e => setDraft({ ...draft, body: e.currentTarget.value })} />
            <Group justify="flex-end">
              <Button size="xs" variant="default" onClick={() => setDraft(null)}>Cancel</Button>
              <Button size="xs" disabled={!draft.prefix.trim() || !draft.body.trim()}
                onClick={() => commit([
                  ...snippets.filter(s => s.prefix !== draft.prefix),
                  { ...draft, prefix: draft.prefix.trim() },
                ])}>
                Save
              </Button>
            </Group>
          </Stack>
        ) : null}
      </Stack>
    </Modal>
  );
}
