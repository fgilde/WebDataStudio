import { useState } from "react";
import {
  ActionIcon, Badge, Button, Code, Group, Modal, ScrollArea, SegmentedControl, Stack, Table, Text,
  TextInput, Textarea, Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { redisApplyEdit, redisPreviewEdit, type RedisValueDto } from "../api";
import { detectFormat, formatTtl, toHexDump, type ValueFormat } from "./format";

interface EditorProps {
  value: RedisValueDto;
  onEdit: (operation: string, payload: Record<string, unknown>) => void;
  readOnly: boolean;
}

/// One editor per type. A hash is not a list is not a stream, and a single "value" box for all of
/// them is what makes a Redis client useless for anything but strings.
export function ValueEditor({ connectionId, database, value, onChanged, readOnly }: {
  connectionId: string;
  database: number;
  value: RedisValueDto;
  onChanged: () => void;
  readOnly: boolean;
}) {
  const [pending, setPending] = useState<{ hash: string; commands: string[]; destructive: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Every write is previewed: Redis has no transaction to roll back, so the preview is the last
  // place a mistake can be caught.
  const edit = (operation: string, payload: Record<string, unknown>) => {
    setError(null);
    redisPreviewEdit(connectionId, { database, key: value.key, operation, payload })
      .then(setPending)
      .catch(e => setError(e.message));
  };

  const apply = () => {
    if (!pending) return;

    redisApplyEdit(connectionId, pending.hash)
      .then(() => { setPending(null); onChanged(); })
      .catch(e => { setError(e.message); setPending(null); });
  };

  const props: EditorProps = { value, onEdit: edit, readOnly };

  return (
    <Stack gap="xs" h="100%" style={{ minHeight: 0 }}>
      <Group gap={8} wrap="nowrap">
        <Badge size="sm" variant="light">{value.type}</Badge>
        <Text size="sm" fw={600} truncate style={{ flex: 1 }}>{value.key}</Text>
        <Text size="xs" c="dimmed">{value.length} · {formatTtl(value.ttlSeconds)}</Text>
        {value.encoding ? <Badge size="xs" variant="outline">{value.encoding}</Badge> : null}
      </Group>

      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" w={130} placeholder="TTL seconds" disabled={readOnly}
          onKeyDown={event => {
            if (event.key !== "Enter") return;
            const seconds = Number(event.currentTarget.value);
            if (seconds > 0) edit("expire", { seconds });
          }} />
        <Button size="compact-xs" variant="default" disabled={readOnly}
          onClick={() => edit("persist", {})}>Remove expiry</Button>
        <Button size="compact-xs" variant="default" color="red" disabled={readOnly}
          onClick={() => edit("del", {})}>Delete key</Button>
      </Group>

      {error ? <Text size="xs" c="red">{error}</Text> : null}

      <div style={{ flex: 1, minHeight: 0 }}>
        {value.type === "string" ? <StringEditor {...props} />
          : value.type === "hash" ? <HashEditor {...props} />
          : value.type === "list" ? <ListEditor {...props} />
          : value.type === "set" ? <SetEditor {...props} />
          : value.type === "zset" ? <SortedSetEditor {...props} />
          : value.type === "stream" ? <StreamEditor {...props} />
          : <Text size="xs" c="dimmed">This type can be read but not edited from here.</Text>}
      </div>

      {/* The commands, before they run. This is the same handshake the data grid uses. */}
      <Modal opened={pending !== null} onClose={() => setPending(null)} title="Apply this change?">
        <Stack gap="sm">
          {pending?.destructive
            ? <Text size="xs" c="red">This removes data. Redis has no undo for it.</Text>
            : null}
          <Code block fz="xs">{pending?.commands.join("\n")}</Code>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setPending(null)}>Cancel</Button>
            <Button size="xs" color={pending?.destructive ? "red" : undefined} onClick={apply}>
              Run it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function StringEditor({ value, onEdit, readOnly }: EditorProps) {
  const text = typeof value.value === "string" ? value.value : String(value.value ?? "");
  const [format, setFormat] = useState<ValueFormat>(detectFormat(text));
  const [draft, setDraft] = useState(text);

  const shown = format === "hex"
    ? toHexDump(text)
    : format === "json"
      ? safeJson(text)
      : draft;

  return (
    <Stack gap={6} h="100%">
      <Group gap={6}>
        <SegmentedControl size="xs" value={format} onChange={v => setFormat(v as ValueFormat)}
          data={[{ label: "Text", value: "text" }, { label: "JSON", value: "json" },
            { label: "Hex", value: "hex" }]} />
        <Button size="compact-xs" disabled={readOnly || format !== "text" || draft === text}
          onClick={() => onEdit("set", { value: draft })}>Save</Button>
      </Group>

      <Textarea flex={1} autosize={false} styles={{ input: { height: "100%", fontFamily: "monospace", fontSize: 12 } }}
        readOnly={format !== "text" || readOnly} value={shown}
        onChange={event => setDraft(event.currentTarget.value)} />
    </Stack>
  );
}

function HashEditor({ value, onEdit, readOnly }: EditorProps) {
  const entries = Object.entries((value.value ?? {}) as Record<string, string>);
  const [field, setField] = useState("");
  const [fresh, setFresh] = useState("");

  return (
    <Stack gap={6} h="100%">
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" placeholder="field" value={field}
          onChange={event => setField(event.currentTarget.value)} />
        <TextInput size="xs" flex={1} placeholder="value" value={fresh}
          onChange={event => setFresh(event.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Add field" disabled={readOnly || !field}
          onClick={() => onEdit("hset", { field, value: fresh })}>
          <IconPlus size={14} />
        </ActionIcon>
      </Group>

      <ScrollArea flex={1}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {entries.map(([name, entry]) => (
              <Table.Tr key={name}>
                <Table.Td w={180}><Text size="xs" fw={600} truncate>{name}</Text></Table.Td>
                <Table.Td>
                  <EditableText value={entry} readOnly={readOnly}
                    onCommit={next => onEdit("hset", { field: name, value: next })} />
                </Table.Td>
                <Table.Td w={30}>
                  <ActionIcon size="sm" variant="subtle" color="red" disabled={readOnly}
                    aria-label={`Delete ${name}`} onClick={() => onEdit("hdel", { field: name })}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function ListEditor({ value, onEdit, readOnly }: EditorProps) {
  const items = (value.value ?? []) as string[];
  const [fresh, setFresh] = useState("");

  return (
    <Stack gap={6} h="100%">
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="value" value={fresh}
          onChange={event => setFresh(event.currentTarget.value)} />
        <Button size="compact-xs" variant="default" disabled={readOnly}
          onClick={() => onEdit("lpush", { value: fresh })}>Push left</Button>
        <Button size="compact-xs" variant="default" disabled={readOnly}
          onClick={() => onEdit("rpush", { value: fresh })}>Push right</Button>
      </Group>

      <ScrollArea flex={1}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {items.map((item, index) => (
              <Table.Tr key={`${index}:${item}`}>
                <Table.Td w={50}><Text size="10px" c="dimmed">{index}</Text></Table.Td>
                <Table.Td>
                  <EditableText value={item} readOnly={readOnly}
                    onCommit={next => onEdit("lset", { index, value: next })} />
                </Table.Td>
                <Table.Td w={30}>
                  {/* By value, not by index: Redis removes by value, and a list that shifted
                      between the read and the write would otherwise delete the wrong entry. */}
                  <ActionIcon size="sm" variant="subtle" color="red" disabled={readOnly}
                    aria-label={`Remove ${item}`} onClick={() => onEdit("lrem", { value: item })}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function SetEditor({ value, onEdit, readOnly }: EditorProps) {
  const members = (value.value ?? []) as string[];
  const [fresh, setFresh] = useState("");

  return (
    <Stack gap={6} h="100%">
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="member" value={fresh}
          onChange={event => setFresh(event.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Add member" disabled={readOnly || !fresh}
          onClick={() => onEdit("sadd", { value: fresh })}>
          <IconPlus size={14} />
        </ActionIcon>
      </Group>

      <ScrollArea flex={1}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {members.map(member => (
              <Table.Tr key={member}>
                <Table.Td><Text size="xs">{member}</Text></Table.Td>
                <Table.Td w={30}>
                  <ActionIcon size="sm" variant="subtle" color="red" disabled={readOnly}
                    aria-label={`Remove ${member}`} onClick={() => onEdit("srem", { value: member })}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function SortedSetEditor({ value, onEdit, readOnly }: EditorProps) {
  const entries = (value.value ?? []) as { member: string; score: number }[];
  const [member, setMember] = useState("");
  const [score, setScore] = useState("0");

  return (
    <Stack gap={6} h="100%">
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" w={90} placeholder="score" value={score}
          onChange={event => setScore(event.currentTarget.value)} />
        <TextInput size="xs" flex={1} placeholder="member" value={member}
          onChange={event => setMember(event.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Add member" disabled={readOnly || !member}
          onClick={() => onEdit("zadd", { member, score })}>
          <IconPlus size={14} />
        </ActionIcon>
      </Group>

      <ScrollArea flex={1}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {entries.map(entry => (
              <Table.Tr key={entry.member}>
                <Table.Td w={90}>
                  <EditableText value={String(entry.score)} readOnly={readOnly}
                    onCommit={next => onEdit("zadd", { member: entry.member, score: next })} />
                </Table.Td>
                <Table.Td><Text size="xs" truncate>{entry.member}</Text></Table.Td>
                <Table.Td w={30}>
                  <ActionIcon size="sm" variant="subtle" color="red" disabled={readOnly}
                    aria-label={`Remove ${entry.member}`}
                    onClick={() => onEdit("zrem", { member: entry.member })}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function StreamEditor({ value, onEdit, readOnly }: EditorProps) {
  const entries = (value.value ?? []) as { id: string; values: Record<string, string> }[];
  const [field, setField] = useState("");
  const [fresh, setFresh] = useState("");

  return (
    <Stack gap={6} h="100%">
      <Group gap={6} wrap="nowrap">
        <TextInput size="xs" w={130} placeholder="field" value={field}
          onChange={event => setField(event.currentTarget.value)} />
        <TextInput size="xs" flex={1} placeholder="value" value={fresh}
          onChange={event => setFresh(event.currentTarget.value)} />
        <Tooltip label="Append an entry with a server-generated id">
          <ActionIcon size="sm" variant="subtle" aria-label="Add entry" disabled={readOnly || !field}
            onClick={() => onEdit("xadd", { [field]: fresh })}>
            <IconPlus size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>

      <ScrollArea flex={1}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {entries.map(entry => (
              <Table.Tr key={entry.id}>
                <Table.Td w={170}><Text size="10px" c="dimmed">{entry.id}</Text></Table.Td>
                <Table.Td>
                  <Text size="xs" style={{ fontFamily: "monospace" }}>
                    {Object.entries(entry.values).map(([name, item]) => `${name}=${item}`).join("  ")}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

/// Click to edit, Enter to commit, Escape to give up. The commit goes through the preview like
/// everything else, so a typo is still catchable.
function EditableText({ value, readOnly, onCommit }: {
  value: string;
  readOnly: boolean;
  onCommit: (next: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);

  if (!editing)
    return (
      <Text size="xs" truncate style={{ cursor: readOnly ? "default" : "text" }}
        onDoubleClick={() => { if (!readOnly) { setDraft(value); setEditing(true); } }}>
        {value}
      </Text>
    );

  return (
    <TextInput size="xs" autoFocus value={draft}
      onChange={event => setDraft(event.currentTarget.value)}
      onBlur={() => setEditing(false)}
      onKeyDown={event => {
        if (event.key === "Enter") { setEditing(false); if (draft !== value) onCommit(draft); }
        if (event.key === "Escape") setEditing(false);
      }} />
  );
}

const safeJson = (text: string) => {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
};
