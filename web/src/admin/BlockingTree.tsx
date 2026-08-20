import { Alert, Badge, Button, Group, Text } from "@mantine/core";
import { killSession, type LockWaitDto } from "../api";
import { chainSize, toChains, type ChainNode } from "./blockingChains";

/// Who is holding up whom, as a tree. A flat list answers "who is waiting"; the tree answers "who to
/// kill", and that is always the session at the root — killing a waiter changes nothing.
export function BlockingTree({ connectionId, waits }: {
  connectionId: string;
  waits: LockWaitDto[];
}) {
  if (waits.length === 0) return null;

  const chains = toChains(waits);

  return (
    <Alert color="orange" p={8} title={`${waits.length} sessions are waiting`}>
      {chains.map(chain => (
        <div key={chain.session} style={{ marginBottom: 6 }}>
          <Group gap={6} wrap="nowrap">
            <Badge size="xs" color="orange">root</Badge>
            <Text size="xs" fw={700}>session {chain.session}</Text>
            <Text size="10px" c="dimmed">holds up {chainSize(chain)}</Text>
            <Button size="compact-xs" variant="light" color="red"
              onClick={() => killSession(connectionId, chain.session).catch(() => {})}>
              Kill it
            </Button>
          </Group>

          <Branch nodes={chain.blocked} depth={1} connectionId={connectionId} />
        </div>
      ))}
    </Alert>
  );
}

function Branch({ nodes, depth, connectionId }: {
  nodes: ChainNode[];
  depth: number;
  connectionId: string;
}) {
  return (
    <>
      {nodes.map(node => (
        <div key={node.session} style={{ marginLeft: depth * 14 }}>
          <Group gap={6} wrap="nowrap">
            <Text size="10px" c="dimmed">↳</Text>
            <Text size="xs">session {node.session}</Text>
            <Text size="10px" c="dimmed">
              waiting {Math.round(node.waitMs / 1000)}s{node.resource ? ` on ${node.resource}` : ""}
            </Text>
            {node.statement ? (
              <Text size="10px" truncate maw={320} style={{ fontFamily: "monospace" }}>
                {node.statement}
              </Text>
            ) : null}
            {/* Killing a waiter frees nothing, so it is offered only where it might help: a session
                that is itself blocking somebody further down. */}
            {node.blocked.length > 0 ? (
              <Button size="compact-xs" variant="subtle" color="red"
                onClick={() => killSession(connectionId, node.session).catch(() => {})}>
                Kill
              </Button>
            ) : null}
          </Group>

          <Branch nodes={node.blocked} depth={depth + 1} connectionId={connectionId} />
        </div>
      ))}
    </>
  );
}
