import { Alert, Badge, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconCamera, IconFileText } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { driftScript, schemaDrift, takeSnapshot, type SchemaDriftDto } from "../api";
import { notifications } from "@mantine/notifications";

/// What moved since the schema was last written down — and what to run on the other machine.
///
/// The snapshot is taken on start and on the button here. The interesting question is never "what
/// does the schema look like": it is "what did somebody change since Monday", and right after that,
/// "what do I have to run where this has not happened yet".
export function SchemaDrift({ connectionId, onOpenInEditor }: {
  connectionId: string;
  onOpenInEditor?: (sql: string) => void;
}) {
  const [state, setState] = useState<{ configured: boolean; drift: SchemaDriftDto | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    setState(null);
    schemaDrift(connectionId).then(setState).catch(() => setState({ configured: false, drift: null }));
  }, [connectionId, nonce]);

  const snapshot = async () => {
    setBusy(true);

    try {
      await takeSnapshot();
      setNonce(n => n + 1);
      notifications.show({ message: "snapshot taken" });
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy(false);
    }
  };

  const script = async () => {
    setBusy(true);

    try {
      const built = await driftScript(connectionId);

      if (built.statements === 0 && built.needsAPerson.length === 0) {
        notifications.show({ message: "nothing to carry over: the schema is where the snapshot left it" });
        return;
      }

      // The parts the studio will not write are comments at the top, so the script is still a
      // script and the warnings travel with it.
      const notes = built.needsAPerson.map(line => `-- ${line}`).join("\n");
      onOpenInEditor?.([notes, built.script].filter(part => part.length > 0).join("\n\n"));
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy(false);
    }
  };

  if (!state) return <Loader size="xs" m="sm" />;

  if (!state.configured)
    return (
      <Alert color="gray" variant="light" m="xs" p={8}>
        <Text size="xs">
          No snapshot directory is set. With <code>WDS_SCHEMA_SNAPSHOT_DIR</code> the studio writes
          the shape of every schema on start and reports what moved since.
        </Text>
      </Alert>
    );

  const drift = state.drift;

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6}>
        <Button size="compact-xs" variant="default" leftSection={<IconCamera size={13} />}
          loading={busy} onClick={snapshot}>
          Snapshot now
        </Button>
        <Button size="compact-xs" leftSection={<IconFileText size={13} />} loading={busy}
          disabled={!onOpenInEditor} onClick={script}>
          Script the difference…
        </Button>

        {drift?.before && (
          <Text size="xs" c="dimmed" ml="auto">
            last written down {new Date(drift.before).toLocaleString()}
          </Text>
        )}
      </Group>

      {!drift && (
        <Text size="xs" c="dimmed">
          Nothing recorded yet for this connection. The first snapshot is the baseline; the second
          one is the first that can say what moved.
        </Text>
      )}

      {drift && drift.added.length === 0 && drift.removed.length === 0 && drift.changed.length === 0 && (
        <Text size="xs" c="dimmed">The schema is where the snapshot left it.</Text>
      )}

      {drift && (
        <Stack gap={4}>
          {drift.added.map(name => (
            <Group key={`a-${name}`} gap={6}>
              <Badge size="xs" color="green" variant="light">added</Badge>
              <Text size="xs">{name}</Text>
            </Group>
          ))}
          {drift.removed.map(name => (
            <Group key={`r-${name}`} gap={6}>
              <Badge size="xs" color="red" variant="light">removed</Badge>
              <Text size="xs">{name}</Text>
            </Group>
          ))}
          {drift.changed.map(line => (
            <Group key={`c-${line}`} gap={6} wrap="nowrap" align="flex-start">
              <Badge size="xs" color="yellow" variant="light">changed</Badge>
              <Text size="xs">{line}</Text>
            </Group>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
