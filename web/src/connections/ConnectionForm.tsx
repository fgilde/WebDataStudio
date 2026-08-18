import { useState } from "react";
import { Button, Group, Select, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { ENGINES, engineFromConnectionString } from "./engines";
import { testConnection, type ConnectionInput } from "../api";

export function ConnectionForm({ initial, onSubmit, onCancel }: {
  initial?: ConnectionInput;
  onSubmit: (value: ConnectionInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [value, setValue] = useState<ConnectionInput>(initial ?? {
    name: "", engine: "postgresql", connectionString: "", readOnly: false,
  });
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Pasting a connection string picks the engine automatically.
  const setConnectionString = (text: string) => {
    const detected = engineFromConnectionString(text);
    setValue(v => ({ ...v, connectionString: text, engine: detected ?? v.engine }));
  };

  const test = async () => {
    setBusy(true);
    try { const r = await testConnection(value); setStatus(r.message); }
    catch (e) { setStatus(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  return (
    <Stack>
      <TextInput label="Name" value={value.name} required
        onChange={e => setValue(v => ({ ...v, name: e.currentTarget.value }))} />
      <Select label="Engine" data={ENGINES.map(e => ({ value: e.id, label: e.label }))}
        value={value.engine} onChange={id => id && setValue(v => ({ ...v, engine: id }))} />
      <Textarea label="Connection string" autosize minRows={2} value={value.connectionString}
        onChange={e => setConnectionString(e.currentTarget.value)}
        description="A provider connection string or a URL such as postgres://user:pw@host:5432/db" />
      <Switch label="Read-only" checked={value.readOnly}
        onChange={e => setValue(v => ({ ...v, readOnly: e.currentTarget.checked }))} />
      {status && <Text size="sm">{status}</Text>}
      <Group justify="space-between">
        <Button variant="default" onClick={test} loading={busy}>Test</Button>
        <Group>
          <Button variant="subtle" onClick={onCancel}>Cancel</Button>
          <Button onClick={() => onSubmit(value)} disabled={!value.name || !value.connectionString}>Save</Button>
        </Group>
      </Group>
    </Stack>
  );
}
