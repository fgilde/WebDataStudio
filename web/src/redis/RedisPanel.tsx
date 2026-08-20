import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Code, Group, Loader, Modal, ScrollArea, Select, Stack, Table,
  Tabs, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconRefresh, IconSearch, IconTrash } from "@tabler/icons-react";
import {
  redisAnalysis, redisApplyBulk, redisDatabases, redisKeys, redisPreviewBulk, redisPublish,
  redisSlowLog, redisStream, redisSubscribeUrl, redisValue,
  type RedisAnalysisDto, type RedisKeyDto, type RedisSlowEntryDto, type RedisStreamDto,
  type RedisValueDto,
} from "../api";
import { ValueEditor } from "./ValueEditor";
import { formatBytes, formatTtl } from "./format";

const TYPES = ["", "string", "hash", "list", "set", "zset", "stream"];

/// Redis, as a Redis user expects it: a keyspace to walk, a value to edit in the shape it has, and
/// the handful of administrative views that answer why it is slow or large. The command console in
/// a query tab stays where it is — this is the other half.
export function RedisPanel({ connectionId, readOnly }: { connectionId: string; readOnly: boolean }) {
  const [databases, setDatabases] = useState<{ database: number; keys: number }[]>([]);
  const [database, setDatabase] = useState(0);

  useEffect(() => {
    redisDatabases(connectionId).then(setDatabases).catch(() => setDatabases([]));
  }, [connectionId]);

  return (
    <Tabs defaultValue="browser" h="100%" styles={{ panel: { height: "calc(100% - 34px)", minHeight: 0 } }}>
      <Tabs.List>
        <Tabs.Tab value="browser">Keys</Tabs.Tab>
        <Tabs.Tab value="analysis">Analysis</Tabs.Tab>
        <Tabs.Tab value="pubsub">Pub/Sub</Tabs.Tab>
        <Tabs.Tab value="slowlog">Slow log</Tabs.Tab>
      </Tabs.List>

      <Tabs.Panel value="browser">
        <KeyBrowser connectionId={connectionId} database={database} readOnly={readOnly}
          databases={databases} onDatabaseChange={setDatabase} />
      </Tabs.Panel>
      <Tabs.Panel value="analysis">
        <Analysis connectionId={connectionId} database={database} />
      </Tabs.Panel>
      <Tabs.Panel value="pubsub">
        <PubSub connectionId={connectionId} readOnly={readOnly} />
      </Tabs.Panel>
      <Tabs.Panel value="slowlog">
        <SlowLog connectionId={connectionId} />
      </Tabs.Panel>
    </Tabs>
  );
}

function KeyBrowser({ connectionId, database, readOnly, databases, onDatabaseChange }: {
  connectionId: string;
  database: number;
  readOnly: boolean;
  databases: { database: number; keys: number }[];
  onDatabaseChange: (database: number) => void;
}) {
  const [match, setMatch] = useState("*");
  const [type, setType] = useState("");
  const [keys, setKeys] = useState<RedisKeyDto[]>([]);
  const [cursor, setCursor] = useState(0);
  const [complete, setComplete] = useState(true);
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [value, setValue] = useState<RedisValueDto | null>(null);
  const [stream, setStream] = useState<RedisStreamDto | null>(null);
  const [bulk, setBulk] = useState<{ hash: string; matchedKeys: number; sample: string[]; action: string } | null>(null);
  const [error, setError] = useState<string | null>(null);

  // A page at a time, with the cursor Redis handed back: a keyspace is not a table and cannot be
  // counted first.
  const load = useCallback((from: number) => {
    setLoading(true);
    redisKeys(connectionId, { db: database, match, type: type || undefined, cursor: from, count: 200 })
      .then(page => {
        setKeys(current => (from === 0 ? page.keys : [...current, ...page.keys]));
        setCursor(page.nextCursor);
        setComplete(page.complete);
        setError(null);
      })
      .catch(e => setError(e.message))
      .finally(() => setLoading(false));
  }, [connectionId, database, match, type]);

  useEffect(() => { load(0); }, [load]);

  const open = useCallback((key: string) => {
    setSelected(key);
    setStream(null);
    redisValue(connectionId, key, database)
      .then(found => {
        setValue(found);
        if (found.type === "stream")
          redisStream(connectionId, key, database).then(setStream).catch(() => setStream(null));
      })
      .catch(e => setError(e.message));
  }, [connectionId, database]);

  const previewBulk = (action: string) => {
    redisPreviewBulk(connectionId, {
      database, match, type: type || null, action,
      ttlSeconds: action === "expire" ? 3600 : null,
    })
      .then(preview => setBulk({ ...preview, action }))
      .catch(e => setError(e.message));
  };

  return (
    <div style={{ display: "flex", height: "100%", minHeight: 0 }}>
      <div style={{ width: 380, minWidth: 260, borderRight: "1px solid var(--mantine-color-default-border)",
        display: "flex", flexDirection: "column" }}>
        <Stack gap={4} p={4}>
          <Group gap={4} wrap="nowrap">
            <Select size="xs" w={110} value={String(database)} allowDeselect={false}
              data={(databases.length > 0 ? databases : [{ database: 0, keys: 0 }])
                .map(entry => ({ value: String(entry.database), label: `db${entry.database} · ${entry.keys}` }))}
              onChange={next => onDatabaseChange(Number(next ?? 0))} />
            <Select size="xs" w={100} value={type} allowDeselect={false}
              data={TYPES.map(entry => ({ value: entry, label: entry === "" ? "any type" : entry }))}
              onChange={next => setType(next ?? "")} />
            <Tooltip label="Scan again">
              <ActionIcon size="sm" variant="subtle" aria-label="Rescan" onClick={() => load(0)}>
                <IconRefresh size={14} />
              </ActionIcon>
            </Tooltip>
          </Group>

          <TextInput size="xs" leftSection={<IconSearch size={13} />} placeholder="user:*"
            value={match} onChange={event => setMatch(event.currentTarget.value)} />

          <Group gap={4} wrap="nowrap">
            <Button size="compact-xs" variant="default" color="red" disabled={readOnly}
              onClick={() => previewBulk("delete")}>Delete matching…</Button>
            <Button size="compact-xs" variant="default" disabled={readOnly}
              onClick={() => previewBulk("expire")}>Expire matching…</Button>
          </Group>
        </Stack>

        {error ? <Alert color="red" p={6} m={4}><Text size="xs">{error}</Text></Alert> : null}

        <ScrollArea style={{ flex: 1, minHeight: 0 }}>
          <Table fz="xs" striped highlightOnHover withRowBorders={false}>
            <Table.Tbody>
              {keys.map(key => (
                <Table.Tr key={key.key} style={{ cursor: "pointer" }}
                  bg={key.key === selected ? "var(--mantine-color-default)" : undefined}
                  onClick={() => open(key.key)}>
                  <Table.Td>
                    <Text size="xs" truncate>{key.key}</Text>
                    <Group gap={6}>
                      <Text size="10px" c="dimmed">{key.type}</Text>
                      <Text size="10px" c="dimmed">{formatBytes(key.sizeBytes)}</Text>
                      {key.ttlSeconds !== null
                        ? <Text size="10px" c="orange">{formatTtl(key.ttlSeconds)}</Text>
                        : null}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          <Group gap={6} p={6}>
            {loading ? <Loader size="xs" /> : null}
            {complete
              ? <Text size="10px" c="dimmed">{keys.length} keys · scan complete</Text>
              : (
                <Button size="compact-xs" variant="subtle" onClick={() => load(cursor)}>
                  Load more ({keys.length} so far)
                </Button>
              )}
          </Group>
        </ScrollArea>
      </div>

      <div style={{ flex: 1, minWidth: 0, padding: 8, display: "flex", flexDirection: "column" }}>
        {value === null
          ? <Text size="xs" c="dimmed">Pick a key to see and edit its value.</Text>
          : (
            <Stack gap="xs" h="100%" style={{ minHeight: 0 }}>
              <ValueEditor connectionId={connectionId} database={database} value={value}
                readOnly={readOnly} onChanged={() => { open(value.key); load(0); }} />

              {/* A stream's consumer groups are the reason streams exist: what is stuck, and where. */}
              {stream ? (
                <div>
                  <Text size="xs" fw={600} mb={4}>Consumer groups</Text>
                  <Table fz="xs" striped>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Group</Table.Th><Table.Th>Consumers</Table.Th>
                        <Table.Th>Pending</Table.Th><Table.Th>Last delivered</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {stream.groups.map(group => (
                        <Table.Tr key={group.name}>
                          <Table.Td>{group.name}</Table.Td>
                          <Table.Td>{group.consumers}</Table.Td>
                          <Table.Td>{group.pending}</Table.Td>
                          <Table.Td>{group.lastDelivered}</Table.Td>
                        </Table.Tr>
                      ))}
                      {stream.groups.length === 0 ? (
                        <Table.Tr><Table.Td colSpan={4}>
                          <Text size="xs" c="dimmed">No consumer groups.</Text>
                        </Table.Td></Table.Tr>
                      ) : null}
                    </Table.Tbody>
                  </Table>

                  {stream.pending.length > 0 ? (
                    <Text size="10px" c="dimmed" mt={4}>
                      {stream.pending.length} pending entries, oldest idle for{" "}
                      {Math.round(Math.max(...stream.pending.map(entry => entry.idleMs)) / 1000)}s
                    </Text>
                  ) : null}
                </div>
              ) : null}
            </Stack>
          )}
      </div>

      {/* What a pattern is about to hit, before it hits it. */}
      <Modal opened={bulk !== null} onClose={() => setBulk(null)}
        title={bulk ? `${bulk.action === "delete" ? "Delete" : "Expire"} ${bulk.matchedKeys} keys?` : ""}>
        <Stack gap="sm">
          <Text size="xs" c={bulk?.action === "delete" ? "red" : undefined}>
            {bulk?.action === "delete"
              ? "These keys are removed for good; Redis has no undo."
              : "These keys get a one-hour expiry."}
          </Text>
          <Code block fz="xs">{bulk?.sample.join("\n")}
            {bulk && bulk.matchedKeys > bulk.sample.length
              ? `\n… and ${bulk.matchedKeys - bulk.sample.length} more`
              : ""}
          </Code>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setBulk(null)}>Cancel</Button>
            <Button size="xs" color={bulk?.action === "delete" ? "red" : undefined}
              leftSection={bulk?.action === "delete" ? <IconTrash size={13} /> : undefined}
              onClick={() => {
                if (!bulk) return;
                redisApplyBulk(connectionId, bulk.hash)
                  .then(() => { setBulk(null); load(0); })
                  .catch(e => { setError(e.message); setBulk(null); });
              }}>
              Run it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}

function Analysis({ connectionId, database }: { connectionId: string; database: number }) {
  const [analysis, setAnalysis] = useState<RedisAnalysisDto | null>(null);
  const [loading, setLoading] = useState(false);

  const run = useCallback(() => {
    setLoading(true);
    redisAnalysis(connectionId, database)
      .then(setAnalysis)
      .catch(() => setAnalysis(null))
      .finally(() => setLoading(false));
  }, [connectionId, database]);

  useEffect(() => { run(); }, [run]);

  if (loading && analysis === null)
    return <Group gap={6} p="sm"><Loader size="xs" /><Text size="xs" c="dimmed">Sampling…</Text></Group>;

  if (analysis === null) return <Text size="xs" c="dimmed" p="sm">No analysis available.</Text>;

  const widest = Math.max(...analysis.prefixes.map(prefix => prefix.bytes), 1);

  return (
    <ScrollArea h="100%" p="xs">
      <Group gap="lg" mb="sm">
        <Stat label="Keys" value={String(analysis.totalKeys ?? "—")} />
        <Stat label="Memory" value={formatBytes(analysis.totalMemoryBytes)} />
        <Stat label="Sampled" value={`${analysis.sampledKeys}${analysis.complete ? " (all)" : ""}`} />
        <Button size="compact-xs" variant="default" onClick={run}>Sample again</Button>
      </Group>

      <Text size="xs" fw={600} mb={4}>Memory by prefix</Text>
      <Table fz="xs" striped>
        <Table.Tbody>
          {analysis.prefixes.map(prefix => (
            <Table.Tr key={prefix.prefix}>
              <Table.Td w={160}><Text size="xs" truncate>{prefix.prefix}</Text></Table.Td>
              <Table.Td w={80}><Text size="xs" c="dimmed">{prefix.keys} keys</Text></Table.Td>
              <Table.Td w={90}>{formatBytes(prefix.bytes)}</Table.Td>
              <Table.Td>
                {/* A bar rather than a chart: one glance answers which prefix grew. */}
                <div style={{
                  height: 8, borderRadius: 4, background: "var(--mantine-primary-color-filled)",
                  width: `${Math.max(2, (prefix.bytes / widest) * 100)}%`,
                }} />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      <Group align="flex-start" gap="lg" mt="md" wrap="wrap">
        <div style={{ minWidth: 240 }}>
          <Text size="xs" fw={600} mb={4}>Types</Text>
          <Table fz="xs" striped>
            <Table.Tbody>
              {analysis.types.map(type => (
                <Table.Tr key={type.type}>
                  <Table.Td><Badge size="xs" variant="light">{type.type}</Badge></Table.Td>
                  <Table.Td>{type.keys}</Table.Td>
                  <Table.Td>{formatBytes(type.bytes)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </div>

        <div style={{ minWidth: 320, flex: 1 }}>
          <Text size="xs" fw={600} mb={4}>Largest keys</Text>
          <Table fz="xs" striped>
            <Table.Tbody>
              {analysis.largest.map(key => (
                <Table.Tr key={key.key}>
                  <Table.Td><Text size="xs" truncate maw={280}>{key.key}</Text></Table.Td>
                  <Table.Td w={70}><Text size="10px" c="dimmed">{key.type}</Text></Table.Td>
                  <Table.Td w={90}>{formatBytes(key.sizeBytes)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </div>

        <div style={{ minWidth: 280, flex: 1 }}>
          <Text size="xs" fw={600} mb={4}>Expiring soonest</Text>
          <Table fz="xs" striped>
            <Table.Tbody>
              {analysis.expiringSoon.map(key => (
                <Table.Tr key={key.key}>
                  <Table.Td><Text size="xs" truncate maw={240}>{key.key}</Text></Table.Td>
                  <Table.Td w={90}><Text size="xs" c="orange">{formatTtl(key.ttlSeconds)}</Text></Table.Td>
                </Table.Tr>
              ))}
              {analysis.expiringSoon.length === 0 ? (
                <Table.Tr><Table.Td>
                  <Text size="xs" c="dimmed">Nothing in the sample expires.</Text>
                </Table.Td></Table.Tr>
              ) : null}
            </Table.Tbody>
          </Table>
        </div>
      </Group>
    </ScrollArea>
  );
}

function PubSub({ connectionId, readOnly }: { connectionId: string; readOnly: boolean }) {
  const [channels, setChannels] = useState("*");
  const [subscribed, setSubscribed] = useState(false);
  const [messages, setMessages] = useState<{ channel: string; message: string; at: string }[]>([]);
  const [publishChannel, setPublishChannel] = useState("");
  const [publishMessage, setPublishMessage] = useState("");

  useEffect(() => {
    if (!subscribed) return;

    // EventSource, because the server sends the subscription as server-sent events and the browser
    // already knows how to hold that open.
    const source = new EventSource(redisSubscribeUrl(connectionId, channels));

    source.onmessage = event => {
      const payload = JSON.parse(event.data) as { channel: string; message: string; at: string };
      // Newest first, capped: a busy channel would otherwise grow the page until it dies.
      setMessages(current => [payload, ...current].slice(0, 500));
    };
    source.onerror = () => setSubscribed(false);

    return () => source.close();
  }, [subscribed, connectionId, channels]);

  return (
    <Stack gap={6} p="xs" h="100%" style={{ minHeight: 0 }}>
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" flex={1} label="Channels (patterns, comma separated)" value={channels}
          onChange={event => setChannels(event.currentTarget.value)} disabled={subscribed} />
        <Button size="compact-xs" mt={18} variant={subscribed ? "filled" : "default"}
          onClick={() => setSubscribed(current => !current)}>
          {subscribed ? "Stop" : "Subscribe"}
        </Button>
      </Group>

      <Group gap={6} wrap="nowrap" align="flex-end">
        <TextInput size="xs" w={180} label="Publish to" value={publishChannel}
          onChange={event => setPublishChannel(event.currentTarget.value)} />
        <TextInput size="xs" flex={1} label="Message" value={publishMessage}
          onChange={event => setPublishMessage(event.currentTarget.value)} />
        <Button size="compact-xs" disabled={readOnly || !publishChannel}
          onClick={() => redisPublish(connectionId, publishChannel, publishMessage).catch(() => {})}>
          Publish
        </Button>
      </Group>

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {messages.map((message, index) => (
              <Table.Tr key={`${message.at}:${index}`}>
                <Table.Td w={90}>
                  <Text size="10px" c="dimmed">
                    {new Date(message.at).toLocaleTimeString()}
                  </Text>
                </Table.Td>
                <Table.Td w={160}><Text size="xs" fw={600} truncate>{message.channel}</Text></Table.Td>
                <Table.Td><Text size="xs" style={{ fontFamily: "monospace" }}>{message.message}</Text></Table.Td>
              </Table.Tr>
            ))}
            {messages.length === 0 ? (
              <Table.Tr><Table.Td>
                <Text size="xs" c="dimmed">
                  {subscribed ? "Listening…" : "Subscribe to see messages as they arrive."}
                </Text>
              </Table.Td></Table.Tr>
            ) : null}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function SlowLog({ connectionId }: { connectionId: string }) {
  const [entries, setEntries] = useState<RedisSlowEntryDto[]>([]);
  const [loading, setLoading] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    redisSlowLog(connectionId)
      .then(setEntries)
      .catch(() => setEntries([]))
      .finally(() => setLoading(false));
  }, [connectionId]);

  useEffect(() => { load(); }, [load]);

  return (
    <Stack gap={6} p="xs" h="100%" style={{ minHeight: 0 }}>
      <Group gap={6}>
        <Button size="compact-xs" variant="default" onClick={load}>Reload</Button>
        {loading ? <Loader size="xs" /> : null}
        <Text size="10px" c="dimmed">
          The threshold is the server's own slowlog-log-slower-than; an empty list means nothing was
          slower than that.
        </Text>
      </Group>

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table fz="xs" striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={140}>When</Table.Th><Table.Th w={90}>Took</Table.Th><Table.Th>Command</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {entries.map(entry => (
              <Table.Tr key={entry.id}>
                <Table.Td><Text size="10px" c="dimmed">{new Date(entry.at).toLocaleString()}</Text></Table.Td>
                <Table.Td>{(entry.microSeconds / 1000).toFixed(1)} ms</Table.Td>
                <Table.Td><Text size="xs" style={{ fontFamily: "monospace" }} truncate>{entry.command}</Text></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

const Stat = ({ label, value }: { label: string; value: string }) => (
  <div>
    <Text size="10px" c="dimmed" tt="uppercase">{label}</Text>
    <Text size="sm" fw={700}>{value}</Text>
  </div>
);
