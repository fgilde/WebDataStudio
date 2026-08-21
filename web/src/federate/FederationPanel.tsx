import { useState } from "react";
import {
  ActionIcon, Alert, Button, Code, Group, NumberInput, ScrollArea, Select, Stack, Text, Textarea,
  Tooltip,
} from "@mantine/core";
import { IconPlayerPlay, IconPlus, IconTrash, IconWand } from "@tabler/icons-react";
import type { Connection } from "../api";
import { ResultArea } from "../query/ResultArea";
import { applyChunk, createResultState, type ResultState } from "../query/resultStore";
import { previewFederation, runFederation, type FederationSource } from "./runFederation";

const emptySource = (): FederationSource & { key: string } =>
  ({ key: Math.random().toString(36).slice(2), connectionId: "", sql: "", alias: "" });

/// One query per connection, staged under an alias, then one query over all of them. The staging is
/// visible on purpose: this joins copies, not databases, and the panel says so.
export function FederationPanel({ connections }: { connections: Connection[] }) {
  const [sources, setSources] = useState([{ ...emptySource(), alias: "a" }, { ...emptySource(), alias: "b" }]);
  const [sql, setSql] = useState("SELECT *\n  FROM a\n  JOIN b ON b.id = a.id");
  const [maxRows, setMaxRows] = useState<number | string>(100000);
  const [plan, setPlan] = useState<{ alias: string; ddl: string }[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<ResultState | null>(null);
  const [running, setRunning] = useState(false);

  const options = connections.map(c => ({ value: c.id, label: c.name }));
  const usable = sources.filter(s => s.connectionId && s.sql.trim() && s.alias.trim());
  const request = () => ({
    sources: usable.map(s => ({ connectionId: s.connectionId, sql: s.sql, alias: s.alias })),
    sql,
    maxRowsPerSource: typeof maxRows === "number" ? maxRows : undefined,
  });

  const update = (key: string, patch: Partial<FederationSource>) =>
    setSources(list => list.map(s => (s.key === key ? { ...s, ...patch } : s)));

  const preview = async () => {
    setError(null);
    setPlan(null);
    try {
      setPlan((await previewFederation(request())).sources);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const run = async () => {
    setError(null);
    setPlan(null);
    setRunning(true);
    let state = createResultState();
    setResult(state);
    try {
      await runFederation(request(), chunk => {
        state = applyChunk(state, chunk);
        setResult(state);
      });
    } finally {
      setRunning(false);
    }
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Stack gap="xs" p="xs">
        <Text size="xs" c="dimmed">
          Each source query runs on its own connection; its rows are copied into a table named by
          the alias, and the query below runs over those tables.
        </Text>

        {sources.map(source => (
          <Group key={source.key} gap="xs" align="flex-start" wrap="nowrap">
            <Stack gap={4} w={200}>
              <Select size="xs" placeholder="Connection" data={options} searchable
                value={source.connectionId || null}
                onChange={value => update(source.key, { connectionId: value ?? "" })} />
              <Textarea size="xs" placeholder="Alias" autosize minRows={1} maxRows={1}
                value={source.alias}
                onChange={e => update(source.key, { alias: e.currentTarget.value })} />
            </Stack>
            <Textarea size="xs" flex={1} autosize minRows={2} maxRows={6} styles={{ input: { fontFamily: "monospace" } }}
              placeholder="SELECT … from this connection"
              value={source.sql}
              onChange={e => update(source.key, { sql: e.currentTarget.value })} />
            <Tooltip label="Remove this source">
              <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove source"
                disabled={sources.length <= 1}
                onClick={() => setSources(list => list.filter(s => s.key !== source.key))}>
                <IconTrash size={14} />
              </ActionIcon>
            </Tooltip>
          </Group>
        ))}

        <Group gap="xs">
          <Button size="compact-xs" variant="default" leftSection={<IconPlus size={13} />}
            onClick={() => setSources(list => [...list, emptySource()])}>
            Add source
          </Button>
          <NumberInput size="xs" w={160} min={1} max={1000000} step={10000}
            label={undefined} prefix="" placeholder="Rows per source"
            value={maxRows} onChange={setMaxRows} />
          <Text size="xs" c="dimmed">rows per source at most</Text>
        </Group>

        <Textarea size="xs" autosize minRows={4} maxRows={12}
          styles={{ input: { fontFamily: "monospace" } }}
          label="Query over the staged sources"
          value={sql} onChange={e => setSql(e.currentTarget.value)} />

        <Group gap="xs">
          <Button size="compact-xs" leftSection={<IconPlayerPlay size={13} />} loading={running}
            disabled={usable.length === 0 || !sql.trim()} onClick={run}>
            Run
          </Button>
          <Button size="compact-xs" variant="default" leftSection={<IconWand size={13} />}
            disabled={usable.length === 0} onClick={preview}>
            What would be staged
          </Button>
          {usable.length !== sources.length && (
            <Text size="xs" c="dimmed">
              {sources.length - usable.length} source(s) are incomplete and will be skipped
            </Text>
          )}
        </Group>

        {error && <Alert color="red" p="xs"><Text size="sm">{error}</Text></Alert>}

        {plan && (
          <ScrollArea.Autosize mah={180}>
            <Stack gap={4}>
              {plan.map(entry => (
                <Code key={entry.alias} block style={{ whiteSpace: "pre-wrap" }}>{entry.ddl};</Code>
              ))}
            </Stack>
          </ScrollArea.Autosize>
        )}
      </Stack>

      <div style={{ flex: 1, minHeight: 0 }}>
        {result
          ? <ResultArea result={result} />
          : <Text size="xs" c="dimmed" p="xs">Run the query to see the joined rows here.</Text>}
      </div>
    </div>
  );
}
