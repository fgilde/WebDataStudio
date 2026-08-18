import { useEffect, useState } from "react";
import { Alert, Badge, Button, Group, Modal, ScrollArea, Stack, Text, TextInput } from "@mantine/core";
import { previewChanges, applyChanges, type ChangePreviewDto } from "../../api";
import type { RowChange } from "./useChangeSet";

/// Nothing is written until the user has seen this script and confirmed it. A destructive script
/// additionally requires the table name typed out.
export function ChangePreviewModal({ connectionId, objectRef, tableName, changes, onClose, onApplied }: {
  connectionId: string;
  objectRef: string;
  tableName: string;
  changes: RowChange[] | null;
  onClose: () => void;
  onApplied: () => void;
}) {
  const [preview, setPreview] = useState<ChangePreviewDto | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!changes) { setPreview(null); return; }

    setPreview(null);
    setError(null);
    setConfirmation("");
    previewChanges(connectionId, objectRef, changes).then(setPreview)
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  }, [changes, connectionId, objectRef]);

  if (!changes) return null;

  const needsTypedConfirmation = preview?.destructive === true;
  const confirmed = !needsTypedConfirmation || confirmation.trim() === tableName;

  const apply = async () => {
    if (!preview) return;
    setBusy(true);
    setError(null);
    try {
      await applyChanges(connectionId, objectRef, preview.hash);
      onApplied();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title="Review changes" size="lg">
      <Stack gap="sm">
        {error && <Text c="red" size="sm">{error}</Text>}

        {preview && (
          <>
            <Group gap="xs">
              <Badge variant="light">{preview.statementCount} statements</Badge>
              {preview.destructive && <Badge color="red" variant="light">destructive</Badge>}
            </Group>

            <ScrollArea h={260}>
              <Text size="xs" ff="monospace" style={{ whiteSpace: "pre-wrap" }}>{preview.script}</Text>
            </ScrollArea>

            {needsTypedConfirmation && (
              <Alert color="red" p="xs">
                <Text size="sm">This script deletes rows. Type <b>{tableName}</b> to confirm.</Text>
                <TextInput mt={6} size="xs" value={confirmation}
                  onChange={e => setConfirmation(e.currentTarget.value)} />
              </Alert>
            )}
          </>
        )}

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button onClick={apply} loading={busy} disabled={!preview || !confirmed}>Apply</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
