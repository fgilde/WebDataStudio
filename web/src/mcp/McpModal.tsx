import { useEffect, useMemo, useState } from "react";
import {
  Alert, Badge, Button, Code, CopyButton, Group, Modal, ScrollArea, Stack, Table, Tabs, Text,
} from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { mcpInfo, type McpInfoDto } from "../api";

/// The MCP endpoint, as something somebody can paste into their agent. The point is not to explain
/// the protocol: it is to hand over the two lines of configuration their client wants.
export function McpModal({ opened, onClose, path, needsKey }: {
  opened: boolean;
  onClose: () => void;
  path: string;
  needsKey: boolean;
}) {
  const [info, setInfo] = useState<McpInfoDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!opened) return;
    mcpInfo(path).then(setInfo).catch(e => setError(e instanceof Error ? e.message : String(e)));
  }, [opened, path]);

  const url = useMemo(() => new URL(path, window.location.origin).toString(), [path]);

  const key = needsKey ? "<the value of WDS_MCP_KEY>" : null;

  const snippets = useMemo(() => {
    const headers = key ? { Authorization: `Bearer ${key}` } : undefined;

    return {
      "Claude Code": [
        key
          ? `claude mcp add --transport http webdatastudio ${url} \\\n  --header "Authorization: Bearer ${key}"`
          : `claude mcp add --transport http webdatastudio ${url}`,
      ].join("\n"),
      "Claude Desktop": JSON.stringify({
        mcpServers: {
          webdatastudio: { type: "http", url, ...(headers ? { headers } : {}) },
        },
      }, null, 2),
      "VS Code": JSON.stringify({
        servers: {
          webdatastudio: { type: "http", url, ...(headers ? { headers } : {}) },
        },
      }, null, 2),
      Cursor: JSON.stringify({
        mcpServers: {
          webdatastudio: { url, ...(headers ? { headers } : {}) },
        },
      }, null, 2),
      curl: [
        `curl -s ${url} \\`,
        ...(key ? [`  -H "Authorization: Bearer ${key}" \\`] : []),
        `  -H 'content-type: application/json' \\`,
        `  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'`,
      ].join("\n"),
    };
  }, [url, key]);

  return (
    <Modal opened={opened} onClose={onClose} size="xl"
      title="This studio as an MCP server">
      <Stack gap="sm">
        <Group gap="xs">
          <Code>{url}</Code>
          <CopyButton value={url}>
            {({ copied, copy }) => (
              <Button size="compact-xs" variant="light" onClick={copy}
                leftSection={copied ? <IconCheck size={13} /> : <IconCopy size={13} />}>
                {copied ? "copied" : "copy the URL"}
              </Button>
            )}
          </CopyButton>
          <Badge variant="light" color={info?.writes ? "orange" : "green"}>
            {info?.writes ? "reads and writes" : "read-only"}
          </Badge>
          {needsKey && <Badge variant="light">needs a key</Badge>}
        </Group>

        <Text size="xs" c="dimmed">
          An agent gets the same deal a person gets here: a read-only connection stays read-only, a
          masked column stays masked, and a write is previewed before it runs.
          {info?.writes
            ? " Writing is on, through preview_script and apply_script."
            : " Writing is off; set WDS_MCP_ALLOW_WRITE=true to allow it."}
        </Text>

        {needsKey && (
          <Alert p="xs" color="gray">
            <Text size="xs">
              Replace the placeholder with the key you set in <Code>WDS_MCP_KEY</Code>. The studio
              does not send it to the browser, which is why it cannot fill it in for you.
            </Text>
          </Alert>
        )}

        {error && <Alert color="red" p="xs"><Text size="sm">{error}</Text></Alert>}

        <Tabs defaultValue="Claude Code">
          <Tabs.List>
            {Object.keys(snippets).map(name => (
              <Tabs.Tab key={name} value={name}>{name}</Tabs.Tab>
            ))}
          </Tabs.List>

          {Object.entries(snippets).map(([name, snippet]) => (
            <Tabs.Panel key={name} value={name} pt="xs">
              <Group justify="flex-end" mb={4}>
                <CopyButton value={snippet}>
                  {({ copied, copy }) => (
                    <Button size="compact-xs" variant="subtle" onClick={copy}
                      leftSection={copied ? <IconCheck size={13} /> : <IconCopy size={13} />}>
                      {copied ? "copied" : "copy"}
                    </Button>
                  )}
                </CopyButton>
              </Group>
              <Code block style={{ whiteSpace: "pre-wrap" }}>{snippet}</Code>
            </Tabs.Panel>
          ))}
        </Tabs>

        {info && (
          <>
            <Text size="sm" fw={600}>The tools it offers</Text>
            <ScrollArea.Autosize mah={240}>
              <Table striped>
                <Table.Tbody>
                  {info.tools.map(tool => (
                    <Table.Tr key={tool.name}>
                      <Table.Td width={170}>
                        <Code>{tool.name}</Code>
                        {tool.writes && <Badge size="xs" color="orange" variant="light" ml={4}>writes</Badge>}
                      </Table.Td>
                      <Table.Td><Text size="xs">{tool.description}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea.Autosize>
          </>
        )}
      </Stack>
    </Modal>
  );
}
