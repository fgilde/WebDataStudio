import { useEffect, useRef, useState } from "react";
import {
  ActionIcon, Badge, Button, Code, Group, Loader, Menu, Paper, ScrollArea, Select, Stack, Switch,
  Text, Textarea, Tooltip,
} from "@mantine/core";
import {
  IconMessageChatbot, IconMinus, IconPlus, IconSend, IconSparkles, IconTrash, IconX,
} from "@tabler/icons-react";
import {
  assistCapabilities, assistChat, listConnections, loadWorkspaceItem, saveWorkspaceItem,
  type Connection,
} from "../api";
import {
  deleteSession, listSessions, loadSession, newSession, saveSession, titleOf,
  type ChatSession, type SessionStub,
} from "./chatSessions";

const OPEN_KEY = "chat-open";

/// A chat in the corner, with sessions that survive a reload. It uses the studio's own tools when
/// the MCP endpoint is configured, so it can answer from the database rather than about databases
/// in general — and it says which tools it used.
///
/// Absent unless assistance is configured: a chat that cannot answer is worse than no chat.
export function ChatDock({ onUseStatement }: { onUseStatement?: (sql: string) => void }) {
  const [available, setAvailable] = useState(false);
  const [tools, setTools] = useState<string[] | null>(null);
  const [open, setOpen] = useState(false);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [session, setSession] = useState<ChatSession | null>(null);
  const [sessions, setSessions] = useState<SessionStub[]>([]);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [includeSchema, setIncludeSchema] = useState(true);

  const bottom = useRef<HTMLDivElement>(null);

  useEffect(() => {
    assistCapabilities()
      .then(state => {
        setAvailable(state.configured);
        setTools(state.tools ? state.toolNames : null);
      })
      .catch(() => setAvailable(false));

    listConnections().then(setConnections).catch(() => setConnections([]));
    listSessions().then(setSessions).catch(() => setSessions([]));
    loadWorkspaceItem<boolean>(OPEN_KEY).then(stored => setOpen(stored === true)).catch(() => {});
  }, []);

  // Remembered, because a chat somebody is using should still be there after a reload.
  useEffect(() => {
    if (!available) return;
    void saveWorkspaceItem(OPEN_KEY, open).catch(() => {});
  }, [open, available]);

  useEffect(() => {
    if (!open) return;
    if (session) return;

    // The newest session, or a fresh one on the first connection there is.
    (async () => {
      const index = await listSessions();
      const first = index[0] ? await loadSession(index[0].id) : null;
      setSessions(index);
      setSession(first ?? newSession(null));
    })().catch(() => setSession(newSession(null)));
  }, [open, session]);

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth" });
  }, [session?.messages.length, busy]);

  if (!available) return null;

  const connectionId = session?.connectionId ?? connections[0]?.id ?? null;

  const send = async () => {
    if (!session || !draft.trim() || !connectionId) return;

    const question = draft.trim();
    const asked: ChatSession = {
      ...session,
      connectionId,
      messages: [...session.messages, { role: "user", content: question }],
    };

    setSession(asked);
    setDraft("");
    setError(null);
    setBusy(true);

    try {
      const reply = await assistChat(connectionId,
        asked.messages.map(m => ({ role: m.role, content: m.content })), includeSchema);

      const answered: ChatSession = {
        ...asked,
        title: titleOf(asked.messages),
        messages: [...asked.messages, {
          role: "assistant",
          content: reply.text,
          usedTools: reply.usedTools ?? undefined,
          statements: reply.statements,
        }],
      };

      setSession(answered);
      setSessions(await saveSession(answered));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      // The question stays in the history: retrying it beats retyping it.
    } finally {
      setBusy(false);
    }
  };

  if (!open) {
    return (
      <Tooltip label="Ask the assistant" position="left">
        <ActionIcon size={44} radius="xl" variant="filled" aria-label="Open the chat"
          onClick={() => setOpen(true)}
          style={{ position: "fixed", right: 18, bottom: 18, zIndex: 350 }}>
          <IconMessageChatbot size={22} />
        </ActionIcon>
      </Tooltip>
    );
  }

  return (
    <Paper withBorder shadow="md" radius="md"
      style={{
        position: "fixed", right: 16, bottom: 16, zIndex: 350,
        width: 400, height: 560, display: "flex", flexDirection: "column", overflow: "hidden",
      }}>
      <Group gap={4} p={6} wrap="nowrap"
        style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
        <IconSparkles size={15} />
        <Text size="sm" fw={600} style={{ flex: 1 }} truncate>
          {session ? session.title : "Assistant"}
        </Text>

        <Menu withinPortal position="bottom-end" width={260}>
          <Menu.Target>
            <Tooltip label="Sessions">
              <ActionIcon size="sm" variant="subtle" aria-label="Chat sessions">
                <IconMessageChatbot size={15} />
              </ActionIcon>
            </Tooltip>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconPlus size={13} />}
              onClick={() => setSession(newSession(connectionId))}>
              New chat
            </Menu.Item>
            {sessions.length > 0 && <Menu.Divider />}
            {sessions.map(stub => (
              <Menu.Item key={stub.id}
                rightSection={
                  <ActionIcon size="xs" variant="subtle" color="red" aria-label="Delete session"
                    onClick={async event => {
                      event.stopPropagation();
                      setSessions(await deleteSession(stub.id));
                      if (session?.id === stub.id) setSession(newSession(connectionId));
                    }}>
                    <IconTrash size={12} />
                  </ActionIcon>
                }
                onClick={async () => setSession(await loadSession(stub.id) ?? newSession(connectionId))}>
                <Text size="xs" truncate>{stub.title}</Text>
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>

        <Tooltip label="Minimise">
          <ActionIcon size="sm" variant="subtle" aria-label="Minimise the chat"
            onClick={() => setOpen(false)}><IconMinus size={15} /></ActionIcon>
        </Tooltip>
      </Group>

      <Group gap={4} px={6} py={4} wrap="nowrap">
        <Select size="xs" flex={1} placeholder="connection" searchable
          data={connections.map(c => ({ value: c.id, label: c.name }))}
          value={connectionId}
          onChange={value => setSession(s => (s ? { ...s, connectionId: value } : s))} />
        <Tooltip label="Send the table and column names with the question">
          <Switch size="xs" checked={includeSchema}
            onChange={e => setIncludeSchema(e.currentTarget.checked)} />
        </Tooltip>
        {tools
          ? (
            <Tooltip label={`Can read the database: ${tools.join(", ")}`}>
              <Badge size="xs" variant="light" color="orange">tools</Badge>
            </Tooltip>
          )
          : (
            <Tooltip label="No MCP endpoint, so it cannot read the database itself">
              <Badge size="xs" variant="light" color="gray">no tools</Badge>
            </Tooltip>
          )}
      </Group>

      <ScrollArea style={{ flex: 1, minHeight: 0 }} px={8}>
        <Stack gap="xs" py={6}>
          {session?.messages.length === 0 && (
            <Text size="xs" c="dimmed">
              Ask about this database. {tools
                ? "It can look the schema up and read rows to answer."
                : "It answers from what you tell it; enable the MCP endpoint to let it read."}
            </Text>
          )}

          {session?.messages.map((message, index) => (
            <Paper key={index} withBorder={message.role === "assistant"} p={6} radius="sm"
              bg={message.role === "user" ? "var(--mantine-primary-color-light)" : undefined}>
              {message.usedTools?.length ? (
                <Group gap={3} mb={4}>
                  {[...new Set(message.usedTools)].map(tool => (
                    <Badge key={tool} size="xs" variant="light">{tool}</Badge>
                  ))}
                </Group>
              ) : null}

              <Text size="xs" style={{ whiteSpace: "pre-wrap" }}>{message.content}</Text>

              {message.statements?.length && onUseStatement ? (
                <Stack gap={2} mt={4}>
                  {message.statements.map((statement, position) => (
                    <Group key={position} gap={4} align="flex-start" wrap="nowrap">
                      <Code block flex={1} style={{ whiteSpace: "pre-wrap", fontSize: 10 }}>
                        {statement}
                      </Code>
                      <Button size="compact-xs" variant="light"
                        onClick={() => onUseStatement(statement)}>
                        editor
                      </Button>
                    </Group>
                  ))}
                </Stack>
              ) : null}
            </Paper>
          ))}

          {busy && <Group gap={6}><Loader size="xs" /><Text size="xs" c="dimmed">thinking…</Text></Group>}
          {error && (
            <Group gap={4} wrap="nowrap">
              <IconX size={13} color="var(--mantine-color-red-6)" />
              <Text size="xs" c="red">{error}</Text>
            </Group>
          )}
          <div ref={bottom} />
        </Stack>
      </ScrollArea>

      <Group gap={4} p={6} wrap="nowrap"
        style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
        <Textarea size="xs" flex={1} autosize minRows={1} maxRows={4} placeholder="Ask something…"
          value={draft} onChange={e => setDraft(e.currentTarget.value)}
          onKeyDown={event => {
            // Enter sends, Shift+Enter is a newline — what every chat does.
            if (event.key === "Enter" && !event.shiftKey) {
              event.preventDefault();
              void send();
            }
          }} />
        <ActionIcon variant="filled" aria-label="Send" disabled={!draft.trim() || busy || !connectionId}
          onClick={send}><IconSend size={15} /></ActionIcon>
      </Group>
    </Paper>
  );
}
