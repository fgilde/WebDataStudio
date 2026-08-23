import { useState } from "react";
import { Alert, Button, Group, Modal, Stack, Text, TextInput } from "@mantine/core";
import { IconArchive } from "@tabler/icons-react";
import { saveArchive } from "../api";
import { runJob } from "../shell/jobs";

/// What to keep: a statement's result, or a whole table.
export interface ArchiveTarget {
  connectionId: string;
  sql?: string;
  objectRef?: string;
  /// Prefilled name, usually the table's.
  suggested?: string;
}

/// Not a copy of what is on screen: the archive is read from the database again, so it is the whole
/// result rather than the first page of it.
export function ArchiveDialog({ target, onClose }: {
  target: ArchiveTarget | null;
  onClose: () => void;
}) {
  // Derived rather than copied into state: a fresh target starts with its own suggestion, and
  // nothing has to remember to reset anything.
  const [typed, setTyped] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const name = typed ?? target?.suggested ?? "";

  const close = () => { setTyped(null); setError(null); onClose(); };

  const keep = () => {
    if (!target) return;
    setError(null);

    runJob({ title: "Archive", message: "reading the rows into a file…" },
      () => saveArchive(name.trim(), {
        connectionId: target.connectionId, sql: target.sql, objectRef: target.objectRef,
      }))
      .then(close)
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  };

  return (
    <Modal opened={target !== null} onClose={close} title="Keep this result as an archive"
      size="md">
      <Stack gap="sm">
        <TextInput size="xs" label="Name" placeholder="customers-before-the-migration"
          data-autofocus value={name}
          onChange={event => setTyped(event.currentTarget.value)} />

        <Text size="xs" c="dimmed">
          The rows are read from the database again and written to a file on the studio's disk, as
          NDJSON. Masked columns stay masked in it — an archive of them would be a way around the
          masking. A name that already exists is replaced.
        </Text>

        {error && <Alert color="red" p="xs"><Text size="xs">{error}</Text></Alert>}

        <Group justify="flex-end" gap="xs">
          <Button size="xs" variant="default" onClick={close}>Cancel</Button>
          <Button size="xs" disabled={!name.trim()} onClick={keep}>
            Keep it
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/// The button next to a result, with its own dialog.
export function KeepArchiveButton({ connectionId, sql, objectRef, suggested }: ArchiveTarget) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <Button size="compact-xs" variant="default" leftSection={<IconArchive size={13} />}
        onClick={() => setOpen(true)}>
        Keep
      </Button>

      <ArchiveDialog target={open ? { connectionId, sql, objectRef, suggested } : null}
        onClose={() => setOpen(false)} />
    </>
  );
}
