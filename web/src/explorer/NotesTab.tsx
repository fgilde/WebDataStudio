import { useEffect, useState } from "react";
import {
  ActionIcon, Badge, Button, Group, Loader, ScrollArea, Stack, Text, Textarea,
} from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { addNote, deleteNote, objectNotes, type ObjectNoteDto } from "../api";

/// What people know about an object, next to the object.
///
/// A database has `COMMENT ON`, which needs a DDL right and a migration, so what somebody learns
/// about a table ends up in a chat message instead. This is the studio's own note: a name, a date and
/// a sentence, kept in the workspace next to the query history.
export function NotesTab({ connectionId, objectRef }: {
  connectionId: string;
  objectRef: string;
}) {
  const [notes, setNotes] = useState<ObjectNoteDto[] | null>(null);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    objectNotes(connectionId, objectRef)
      .then(found => { if (!cancelled) { setNotes(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setNotes(null); setDraft(""); };
  }, [connectionId, objectRef]);

  const add = () => {
    if (!draft.trim()) return;

    setBusy(true);
    addNote(connectionId, objectRef, draft.trim())
      .then(note => {
        setNotes(current => [note, ...(current ?? [])]);
        setDraft("");
        setError(null);
      })
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  if (error && notes === null) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (notes === null) return <Loader size="xs" m="xs" />;

  return (
    <ScrollArea h="100%" p="xs">
      <Stack gap="xs">
        <Textarea size="xs" rows={3} value={draft} aria-label="A note about this object"
          placeholder="What should the next person know about this? Why it is shaped this way, what the column really means, what the last migration broke."
          onChange={e => setDraft(e.currentTarget.value)} />

        <Group gap="xs">
          <Button size="compact-xs" loading={busy} disabled={!draft.trim()} onClick={add}>
            Add note
          </Button>
          <Text size="xs" c="dimmed">
            Kept in the studio, not in the database: no DDL right and no migration.
          </Text>
        </Group>

        {error && <Text size="xs" c="red">{error}</Text>}

        {notes.length === 0 && (
          <Text size="xs" c="dimmed">
            Nothing yet. The first note is usually the one somebody else needed a week ago.
          </Text>
        )}

        {notes.map(note => (
          <Stack key={note.id} gap={2}>
            <Group gap="xs" justify="space-between" wrap="nowrap">
              <Group gap={6} wrap="nowrap">
                <Badge size="xs" variant="light">{note.author}</Badge>
                <Text size="10px" c="dimmed">{new Date(note.at).toLocaleString()}</Text>
              </Group>
              <ActionIcon size="sm" variant="subtle" color="red"
                aria-label={`Delete the note from ${note.author}`}
                onClick={() => deleteNote(connectionId, note.id)
                  .then(() => setNotes(current => (current ?? []).filter(one => one.id !== note.id)))
                  .catch(e => setError(e.message))}>
                <IconTrash size={13} />
              </ActionIcon>
            </Group>
            <Text size="xs" style={{ whiteSpace: "pre-wrap" }}>{note.body}</Text>
          </Stack>
        ))}
      </Stack>
    </ScrollArea>
  );
}
