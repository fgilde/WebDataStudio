import { useState } from "react";
import {
  Alert, Button, Checkbox, Group, Modal, Stack, Switch, Text, TextInput,
} from "@mantine/core";
import { bulkGrantStatement } from "../api";

export interface GrantTarget { connectionId: string; schema: string }

/// The privileges a table can carry. The server refuses anything it does not know, so this list is
/// a convenience rather than the guard.
const PRIVILEGES = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

/// "SELECT on everything in this schema for that role", as one script rather than one dialog per
/// table. Like every other change, it is handed to the editor to read before it runs.
export function GrantDialog({ target, onClose, onScript }: {
  target: GrantTarget | null;
  onClose: () => void;
  onScript: (connectionId: string, sql: string) => void;
}) {
  const [grantee, setGrantee] = useState("");
  const [chosen, setChosen] = useState<string[]>(["SELECT"]);
  const [revoke, setRevoke] = useState(false);
  const [future, setFuture] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const build = async () => {
    if (!target) return;
    try {
      const built = await bulkGrantStatement(target.connectionId, {
        schema: target.schema,
        grantee: grantee.trim(),
        privileges: chosen,
        revoke,
        includeFuture: future,
      });
      onScript(target.connectionId, built.sql);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <Modal opened={!!target} onClose={onClose} title={`Privileges on ${target?.schema ?? ""}`}
      size="md">
      <Stack gap="sm">
        <TextInput size="xs" label="Role" placeholder="reporting" value={grantee} data-autofocus
          onChange={e => setGrantee(e.currentTarget.value)} />

        <Checkbox.Group label="Privileges" value={chosen} onChange={setChosen}>
          <Group gap="xs" mt={4}>
            {PRIVILEGES.map(privilege => (
              <Checkbox key={privilege} value={privilege} label={privilege} size="xs" />
            ))}
          </Group>
        </Checkbox.Group>

        <Switch size="xs" label="revoke instead of grant" checked={revoke}
          onChange={e => setRevoke(e.currentTarget.checked)} />
        <Switch size="xs" checked={future} disabled={revoke}
          label="also for tables created later (default privileges)"
          onChange={e => setFuture(e.currentTarget.checked)} />

        <Text size="xs" c="dimmed">
          The statement opens in the editor. Nothing changes until it runs there.
        </Text>

        {error && <Alert color="red" p="xs"><Text size="xs">{error}</Text></Alert>}

        <Group justify="flex-end" gap="xs">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" disabled={!grantee.trim() || chosen.length === 0} onClick={build}>
            Build the script
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
