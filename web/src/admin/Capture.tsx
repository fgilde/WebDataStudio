import { useCallback, useEffect, useRef, useState } from "react";
import {
  Alert, Badge, Button, Group, ScrollArea, Select, Stack, Table, Text, Tooltip,
} from "@mantine/core";
import { captureStatus, startCapture, stopCapture, type CaptureDto } from "../api";

const WINDOWS = ["15", "30", "60", "120", "300"];

/// "Show me what runs on this server in the next minute."
///
/// This is sampling, not tracing: the server's own list of what it is doing, read once a second and
/// grouped by statement. A statement that starts and finishes between two samples is missed, and the
/// panel says so — Extended Events and its equivalents are the real answer and need permissions a
/// studio has no business arranging.
export function Capture({ connectionId }: { connectionId: string }) {
  const [data, setData] = useState<CaptureDto | null>(null);
  const [seconds, setSeconds] = useState("60");
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  const stopPolling = useCallback(() => {
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = null;
  }, []);

  const poll = useCallback(() => {
    stopPolling();
    timer.current = window.setInterval(() => {
      captureStatus(connectionId)
        .then(next => {
          setData(next);
          if (next.state !== "running") stopPolling();
        })
        .catch(() => stopPolling());
    }, 1000);
  }, [connectionId, stopPolling]);

  useEffect(() => {
    // A capture started before this panel was opened keeps running, so its state is read first.
    captureStatus(connectionId)
      .then(current => {
        setData(current);
        if (current.state === "running") poll();
      })
      .catch(e => setError(e.message));

    return stopPolling;
  }, [connectionId, poll, stopPolling]);

  const start = () => {
    setError(null);
    startCapture(connectionId, Number(seconds))
      .then(started => { setData(started); poll(); })
      .catch(e => setError(e.message));
  };

  const running = data?.state === "running";

  return (
    <Stack gap={4} p="xs">
      <Group gap="xs">
        <Select size="xs" w={110} data={WINDOWS.map(value => ({ value, label: `${value} s` }))}
                value={seconds} disabled={running}
                onChange={value => setSeconds(value ?? "60")} />
        {running
          ? <Button size="compact-xs" variant="default" color="red"
                    onClick={() => stopCapture(connectionId).then(setData)}>
              Stop ({data?.secondsLeft}s left)
            </Button>
          : <Button size="compact-xs" onClick={start}>Capture</Button>}

        {data && data.state !== "none" &&
          <Text size="xs" c="dimmed">
            {data.samples} samples · {data.statements.length} statements · {data.state}
          </Text>}
      </Group>

      {error && <Alert color="yellow" variant="light">{error}</Alert>}
      {data?.error && <Alert color="red" variant="light">{data.error}</Alert>}

      <Text size="xs" c="dimmed">
        Sampled once a second: a statement that starts and finishes between two samples is not seen.
      </Text>

      <ScrollArea h={300}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Statement</Table.Th><Table.Th>Longest</Table.Th>
              <Table.Th>Seen</Table.Th><Table.Th>Who</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data?.statements.map(statement => (
              <Table.Tr key={statement.text}>
                <Table.Td style={{ maxWidth: 420, overflow: "hidden", textOverflow: "ellipsis" }}>
                  <Group gap={6} wrap="nowrap">
                    <Tooltip label={statement.text} multiline w={520}>
                      <span>{statement.text.slice(0, 110)}</span>
                    </Tooltip>
                    {statement.blocked && <Badge size="xs" color="red">blocked</Badge>}
                  </Group>
                </Table.Td>
                <Table.Td>{Math.round(statement.maxDurationMs / 100) / 10}s</Table.Td>
                <Table.Td>{statement.samples}×</Table.Td>
                <Table.Td>{statement.users.join(", ") || "—"}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}
