import { useEffect, useState } from "react";
import { Alert, Button, Code, Group, Loader, Modal, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconTablePlus } from "@tabler/icons-react";
import {
  keepResultAsTable, listConnections, planResultTable,
  type Connection, type ResultTablePlanDto,
} from "../api";
import { runJob } from "../shell/jobs";

/// "I have just worked out this join and I will need it again."
///
/// The rows are read from the database again rather than copied off the screen, so what lands in
/// the table is the whole result, not the first page of it.
export function KeepAsTableDialog({ connectionId, sql, onClose, onDone }: {
  connectionId: string;
  sql: string;
  onClose: () => void;
  onDone?: (table: string) => void;
}) {
  const [table, setTable] = useState("");
  const [schema, setSchema] = useState("");
  const [target, setTarget] = useState(connectionId);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [plan, setPlan] = useState<ResultTablePlanDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [planning, setPlanning] = useState(true);

  useEffect(() => {
    let alive = true;
    listConnections().then(list => { if (alive) setConnections(list); }).catch(() => {});
    return () => { alive = false; };
  }, []);

  // What the table would look like, asked again whenever the target changes: another engine means
  // other types.
  useEffect(() => {
    let alive = true;
    setPlanning(true);
    setError(null);

    planResultTable({ connectionId, sql, table: table || "new_table", schema, targetConnectionId: target })
      .then(p => { if (alive) { setPlan(p); setPlanning(false); } })
      .catch(e => {
        if (!alive) return;
        setPlan(null);
        setPlanning(false);
        setError(e instanceof Error ? e.message : String(e));
      });

    return () => { alive = false; };
  }, [connectionId, sql, target, schema, table]);

  const keep = () => {
    setError(null);

    runJob({ title: "New table", message: "reading the rows and writing them…" },
      () => keepResultAsTable({
        connectionId, sql, table: table.trim(), schema: schema.trim() || undefined,
        targetConnectionId: target,
      }))
      .then(outcome => { onDone?.(outcome.table); onClose(); })
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  };

  return (
    <Modal opened onClose={onClose} title="Keep this result as a table" size="lg">
      <Stack gap="sm">
        <Group grow align="flex-start">
          <TextInput size="xs" label="Table name" placeholder="orders_by_month" data-autofocus
            value={table} onChange={event => setTable(event.currentTarget.value)} />
          <TextInput size="xs" label="Schema" placeholder="leave empty for the default"
            value={schema} onChange={event => setSchema(event.currentTarget.value)} />
        </Group>

        <Select size="xs" label="In which connection" value={target}
          onChange={value => setTarget(value ?? connectionId)}
          data={connections.map(c => ({
            value: c.id,
            label: c.id === connectionId ? `${c.name} (this one)` : c.name,
          }))} />

        {planning && <Loader size="xs" />}

        {plan && !planning && (
          <>
            <Text size="xs" c="dimmed">
              {plan.exactTypes
                ? "Same engine on both sides, so the columns keep the types they already have."
                : "Another engine: each column gets the nearest type this one has, widened rather "
                  + "than guessed at. Narrow them afterwards if it matters."}
            </Text>

            <Code block style={{ fontSize: 11, maxHeight: 220, overflow: "auto" }}>
              {plan.createSql}
            </Code>
          </>
        )}

        {error && <Alert color="red" p="xs"><Text size="xs">{error}</Text></Alert>}

        <Group justify="flex-end" gap="xs">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" disabled={!table.trim() || plan === null} onClick={keep}>
            Create it and fill it
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/// The button next to a result, with its own dialog.
export function KeepAsTableButton({ connectionId, sql, onDone }: {
  connectionId: string;
  sql: string;
  onDone?: (table: string) => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <Button size="compact-xs" variant="default" leftSection={<IconTablePlus size={13} />}
        onClick={() => setOpen(true)}>
        As table
      </Button>

      {open && (
        <KeepAsTableDialog connectionId={connectionId} sql={sql}
          onClose={() => setOpen(false)} onDone={onDone} />
      )}
    </>
  );
}
