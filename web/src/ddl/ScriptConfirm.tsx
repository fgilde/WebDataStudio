import { Alert, Button, Code, Group, Modal, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlayerPlay } from "@tabler/icons-react";
import { useState } from "react";
import { applyDdl, type DependencyReportDto } from "../api";
import { notifications } from "@mantine/notifications";

/// What is about to be run, and what it will touch.
export interface PendingScript {
  connectionId: string;
  title: string;
  hash: string;
  script: string;
  /// Marked in red and confirmed as such. A drop, a restart, a disabled trigger.
  destructive?: boolean;
  /// Everything the engine knows reads this object. The point of showing it: a rename or a drop
  /// breaks a view somebody else wrote, and that is worth seeing before rather than after.
  dependencies?: DependencyReportDto;
  /// What running it means. The default is the DDL apply every schema change goes through; the
  /// account panel hands in its own, because those statements are cached under a key of their own.
  apply?: (connectionId: string, hash: string) => Promise<unknown>;
}

/// One statement, shown before it runs.
///
/// Every object editor ends here: the studio writes the DDL, a person reads it, and only then does
/// anything reach the database. It is the same contract the table designer has always had — this is
/// only the one place that draws it.
export function ScriptConfirm({ pending, onClose, onApplied }: {
  pending: PendingScript | null;
  onClose: () => void;
  onApplied?: () => void;
}) {
  const [running, setRunning] = useState(false);

  const run = async () => {
    if (!pending) return;

    setRunning(true);

    try {
      await (pending.apply ?? applyDdl)(pending.connectionId, pending.hash);
      notifications.show({ color: "green", message: pending.title });
      onApplied?.();
      onClose();
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    } finally {
      setRunning(false);
    }
  };

  const usedBy = pending?.dependencies?.usedBy ?? [];

  return (
    <Modal opened={pending !== null} onClose={onClose} title={pending?.title ?? ""} size="lg">
      <Stack gap="sm">
        <Code block style={{ whiteSpace: "pre-wrap", maxHeight: 320, overflow: "auto" }}>
          {pending?.script}
        </Code>

        {usedBy.length > 0 && (
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />} p={8}>
            <Text size="xs">Used by: {usedBy.join(", ")}</Text>
          </Alert>
        )}

        {pending?.destructive && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} p={8}>
            <Text size="xs">This cannot be undone by running it again.</Text>
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" size="xs" onClick={onClose}>Cancel</Button>
          <Button size="xs" color={pending?.destructive ? "red" : undefined} loading={running}
            leftSection={<IconPlayerPlay size={14} />} onClick={run}>
            Run it
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
