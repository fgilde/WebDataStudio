import { useEffect, useState } from "react";
import {
  Alert, Button, Group, ScrollArea, Select, Stack, Table, Text, TextInput,
} from "@mantine/core";
import { notifyListenUrl, sendNotification, type Connection } from "../api";

interface Notification {
  channel: string;
  message: string;
  /// Which backend sent it — the answer to "was that me, or the application?"
  pid: number;
  at: string;
}

/// PostgreSQL's own message bus, watched.
///
/// `LISTEN` and `NOTIFY` are how a PostgreSQL application tells itself something happened: a job
/// queue woke up, a cache should drop a key, a trigger fired. Until now the only way to know
/// whether anything was coming through was to write a client for it.
export function NotifyPanel({ connections, connectionId, onConnectionChange }: {
  connections: Connection[];
  connectionId: string | null;
  onConnectionChange: (id: string) => void;
}) {
  const [channels, setChannels] = useState("");
  const [listening, setListening] = useState(false);
  const [messages, setMessages] = useState<Notification[]>([]);
  const [sendChannel, setSendChannel] = useState("");
  const [sendPayload, setSendPayload] = useState("");
  const [error, setError] = useState<string | null>(null);

  // Only PostgreSQL has this. Naming that beats a panel that is empty for a reason nobody can see.
  const usable = connections.filter(c => c.engine === "postgresql");
  const connection = usable.find(c => c.id === connectionId) ?? usable[0] ?? null;
  const readOnly = connection?.readOnly === true;

  useEffect(() => {
    if (!listening || !connection || !channels.trim()) return;

    // EventSource, because the server sends this as server-sent events and the browser already
    // knows how to hold that open.
    const source = new EventSource(notifyListenUrl(connection.id, channels.trim()));

    source.onmessage = event => {
      const payload = JSON.parse(event.data) as Notification;
      // Newest first, capped: a busy channel would otherwise grow the page until it dies.
      setMessages(current => [payload, ...current].slice(0, 500));
    };

    source.onerror = () => {
      setListening(false);
      setError("the connection to the studio dropped, or this channel cannot be listened on");
    };

    return () => source.close();
  }, [listening, connection, channels]);

  const send = () => {
    if (!connection) return;
    setError(null);

    sendNotification(connection.id, sendChannel, sendPayload)
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  };

  if (usable.length === 0) {
    return (
      <Alert color="gray" m="xs" p="xs">
        <Text size="xs">
          LISTEN/NOTIFY is PostgreSQL's. None of the open connections is one — Redis has the same
          idea, and its own panel.
        </Text>
      </Alert>
    );
  }

  return (
    <Stack gap={6} p="xs" h="100%" style={{ minHeight: 0 }}>
      <Group gap={6} wrap="nowrap" align="flex-end">
        <Select size="xs" w={170} label="Connection" value={connection?.id ?? null}
          onChange={value => value && onConnectionChange(value)}
          data={usable.map(c => ({ value: c.id, label: c.name }))} />

        <TextInput size="xs" flex={1} label="Channels (comma separated)" placeholder="jobs, cache_flush"
          value={channels} onChange={event => setChannels(event.currentTarget.value)}
          disabled={listening} />

        <Button size="compact-xs" variant={listening ? "filled" : "default"}
          disabled={!channels.trim()}
          onClick={() => { setError(null); setListening(current => !current); }}>
          {listening ? "Stop" : "Listen"}
        </Button>
      </Group>

      <Group gap={6} wrap="nowrap" align="flex-end">
        <TextInput size="xs" w={170} label="Send on" value={sendChannel}
          onChange={event => setSendChannel(event.currentTarget.value)} />
        <TextInput size="xs" flex={1} label="Payload" value={sendPayload}
          onChange={event => setSendPayload(event.currentTarget.value)} />
        <Button size="compact-xs" disabled={readOnly || !sendChannel.trim()} onClick={send}>
          Send
        </Button>
      </Group>

      {error && <Alert color="red" p={6}><Text size="xs">{error}</Text></Alert>}

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table fz="xs" striped withRowBorders={false}>
          <Table.Tbody>
            {messages.map((message, index) => (
              <Table.Tr key={`${message.at}:${index}`}>
                <Table.Td w={90}>
                  <Text size="10px" c="dimmed">{new Date(message.at).toLocaleTimeString()}</Text>
                </Table.Td>
                <Table.Td w={150}><Text size="xs" fw={600} truncate>{message.channel}</Text></Table.Td>
                <Table.Td w={60}>
                  <Text size="10px" c="dimmed" title="the backend that sent it">pid {message.pid}</Text>
                </Table.Td>
                <Table.Td>
                  <Text size="xs" style={{ fontFamily: "monospace" }}>{message.message}</Text>
                </Table.Td>
              </Table.Tr>
            ))}

            {messages.length === 0 && (
              <Table.Tr><Table.Td>
                <Text size="xs" c="dimmed">
                  {listening
                    ? "Listening…"
                    : "Name a channel and listen to see notifications as they arrive."}
                </Text>
              </Table.Td></Table.Tr>
            )}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}
