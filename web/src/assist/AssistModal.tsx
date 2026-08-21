import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, Group, Modal, ScrollArea, Stack, Switch, Text, Textarea, Tooltip,
} from "@mantine/core";
import {
  assistAsk, assistCapabilities, assistExplain, assistSql, type AssistReplyDto,
} from "../api";

/// Explains the statement in the editor, or drafts one from a question. Nothing here runs: a
/// suggested statement is put into the editor, where it goes through the same run and the same
/// preview as anything typed by hand.
export function AssistModal({ connectionId, sql, opened, onClose, onUseStatement }: {
  connectionId: string;
  sql: string;
  opened: boolean;
  onClose: () => void;
  onUseStatement: (statement: string) => void;
}) {
  const [question, setQuestion] = useState("");
  const [includeSchema, setIncludeSchema] = useState(false);
  const [reply, setReply] = useState<AssistReplyDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Whether the assistant may look things up itself, which depends on the MCP endpoint.
  const [tools, setTools] = useState<string[] | null>(null);

  useEffect(() => {
    if (!opened) return;

    // Read once per opening: what the studio can do changes with a rollout, not with a click.
    assistCapabilities()
      .then(state => setTools(state.tools ? state.toolNames : null))
      .catch(() => setTools(null));

    return () => {
      // Cleared on the way out rather than on the way in, so a reopened dialog starts empty
      // without a render that shows the previous answer first.
      setReply(null);
      setError(null);
    };
  }, [opened]);

  const call = async (what: "explain" | "draft" | "ask") => {
    setBusy(true);
    setError(null);
    setReply(null);
    try {
      setReply(what === "explain"
        ? await assistExplain(connectionId, sql, includeSchema)
        : what === "ask"
          ? await assistAsk(connectionId, question, includeSchema)
          : await assistSql(connectionId, question, includeSchema));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Ask about this query" size="lg">
      <Stack gap="sm">
        <Alert p="xs" color="gray">
          <Text size="xs">
            What leaves this machine: the statement or the question below, and — only with the switch
            on — the table and column names of this connection. Never a row of data.
          </Text>
        </Alert>

        <Switch size="xs" label="Send the schema (names only)" checked={includeSchema}
          onChange={e => setIncludeSchema(e.currentTarget.checked)} />

        <Textarea size="xs" autosize minRows={2} maxRows={5} label="A question, if you have one"
          placeholder="how many orders had no customer last month?"
          value={question} onChange={e => setQuestion(e.currentTarget.value)} />

        <Group gap="xs">
          <Button size="compact-xs" variant="default" loading={busy} disabled={!sql.trim()}
            onClick={() => call("explain")}>
            Explain the statement
          </Button>
          <Button size="compact-xs" loading={busy} disabled={!question.trim()}
            onClick={() => call("draft")}>
            Draft SQL
          </Button>
          {/* Only when the studio has an MCP endpoint: without it there is nothing to look
              anything up with. */}
          {tools && (
            <Tooltip label={`Uses the studio's own tools: ${tools.join(", ")}`}>
              <Button size="compact-xs" variant="light" color="orange" loading={busy}
                disabled={!question.trim()} onClick={() => call("ask")}>
                Answer it from the database
              </Button>
            </Tooltip>
          )}
        </Group>

        {error && <Alert color="red" p="xs"><Text size="sm">{error}</Text></Alert>}

        {reply && (
          <>
            {reply.usedTools?.length ? (
              <Group gap={4}>
                <Text size="xs" c="dimmed">read the database with</Text>
                {[...new Set(reply.usedTools)].map(tool => (
                  <Badge key={tool} size="xs" variant="light">{tool}</Badge>
                ))}
              </Group>
            ) : null}

            <ScrollArea.Autosize mah={240}>
              <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>{reply.text}</Text>
            </ScrollArea.Autosize>

            {reply.statements.length > 0 && (
              <Stack gap={4}>
                <Badge size="xs" variant="light">{reply.statements.length} statement(s) suggested</Badge>
                {reply.statements.map((statement, index) => (
                  <Group key={index} gap="xs" align="flex-start" wrap="nowrap">
                    <Code block flex={1} style={{ whiteSpace: "pre-wrap" }}>{statement}</Code>
                    <Button size="compact-xs" variant="light"
                      onClick={() => { onUseStatement(statement); onClose(); }}>
                      Put in the editor
                    </Button>
                  </Group>
                ))}
              </Stack>
            )}
          </>
        )}
      </Stack>
    </Modal>
  );
}
