import { useEffect, useState } from "react";
import { Alert, Button, Group, Loader, MultiSelect, Stack, Switch, Text } from "@mantine/core";
import { chooseSchemas, schemaScope, showSystemObjects, type SchemaScopeDto } from "../api";

/// Which schemas this connection reads.
///
/// On a server with five thousand tables, the tree, the completion cache and the object search all
/// pay for every one of them. Somebody who works in two schemas can say so here, and nothing else
/// gets read. Empty means everything, which stays the default.
export function SchemaScopePicker({ connectionId, onChanged }: {
  connectionId: string;
  /// The tree has to be re-read after a change, so the shell is told.
  onChanged?: () => void;
}) {
  const [scope, setScope] = useState<SchemaScopeDto | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;

    schemaScope(connectionId)
      .then(value => {
        if (cancelled) return;
        setScope(value);
        setChosen(value.chosen);
      })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; };
  }, [connectionId]);

  if (error) return <Alert color="yellow" variant="light">{error}</Alert>;
  if (!scope) return <Loader size="xs" />;

  // What the engine keeps for itself: sys and the empty db_owner-style role schemas on SQL Server,
  // pg_catalog on PostgreSQL, SYS on Oracle. Reading a catalogue view is a real errand; having
  // eleven schemas nobody wrote in the tree every day is not, so it is off until asked for.
  const system = (
    <Switch size="xs" label="Show system schemas and their objects"
            checked={scope.systemObjects}
            onChange={event => {
              const show = event.currentTarget.checked;
              setScope({ ...scope, systemObjects: show });
              showSystemObjects(connectionId, show)
                .then(() => onChanged?.())
                .catch(e => setError(e.message));
            }} />
  );

  if (!scope.editable)
    return (
      <Stack gap={4}>
        <Text size="sm" fw={600}>Schemas read</Text>
        <Text size="xs" c="dimmed">
          Fixed by the environment: {scope.fixedByEnvironment.join(", ")}
        </Text>
        {system}
      </Stack>
    );

  const save = () => {
    setSaving(true);
    chooseSchemas(connectionId, chosen)
      .then(() => onChanged?.())
      .catch(e => setError(e.message))
      .finally(() => setSaving(false));
  };

  return (
    <Stack gap={4}>
      <Text size="sm" fw={600}>Schemas read</Text>
      <MultiSelect size="xs" data={scope.available} value={chosen} onChange={setChosen}
                   searchable clearable placeholder="all of them"
                   description="Nothing chosen reads everything. On a large server, naming two schemas is what keeps the tree quick." />
      <Group gap="xs">
        <Button size="compact-xs" onClick={save} loading={saving}>Apply</Button>
        {chosen.length > 0 &&
          <Button size="compact-xs" variant="subtle" onClick={() => { setChosen([]); }}>
            All of them
          </Button>}
      </Group>
      {system}
    </Stack>
  );
}
