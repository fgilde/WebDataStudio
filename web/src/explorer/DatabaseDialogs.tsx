import { useState } from "react";
import { Alert, Button, Group, Modal, Stack, TextInput } from "@mantine/core";
import { createDatabase, dropDatabase } from "../api";

export interface DatabaseTarget { connectionId: string; name?: string }

/// Creating a database is one field; dropping one asks for its name to be typed, because it is
/// the one action here that takes a whole database with it.
export function NewDatabaseDialog({ target, onClose, onDone }: {
  target: DatabaseTarget | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (!target) return;
    setBusy(true);
    setError(null);
    try {
      await createDatabase(target.connectionId, name.trim());
      setName("");
      onClose();
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={target !== null} onClose={onClose} title="New database">
      <Stack gap="sm">
        {error ? <Alert color="red" variant="light">{error}</Alert> : null}
        <TextInput size="xs" label="Name" data-autofocus value={name}
          onChange={e => setName(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === "Enter" && name.trim()) void create(); }} />
        <Group justify="flex-end">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" loading={busy} disabled={!name.trim()} onClick={create}>Create</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function DropDatabaseDialog({ target, onClose, onDone }: {
  target: DatabaseTarget | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const [confirmation, setConfirmation] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const drop = async () => {
    if (!target?.name) return;
    setBusy(true);
    setError(null);
    try {
      await dropDatabase(target.connectionId, target.name);
      setConfirmation("");
      onClose();
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={target !== null} onClose={onClose} title="Drop database">
      <Stack gap="sm">
        <Alert color="red" variant="light">
          Dropping <b>{target?.name}</b> deletes every table in it. This cannot be undone.
        </Alert>
        {error ? <Alert color="red" variant="light">{error}</Alert> : null}
        <TextInput size="xs" data-autofocus label={`Type ${target?.name} to confirm`}
          value={confirmation} onChange={e => setConfirmation(e.currentTarget.value)} />
        <Group justify="flex-end">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" color="red" loading={busy}
            disabled={confirmation.trim() !== target?.name} onClick={drop}>Drop</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/// Text shown when the engine has only one database — better than an action that fails.
export const SINGLE_DATABASE_HINT = "this engine has a single database per connection";
