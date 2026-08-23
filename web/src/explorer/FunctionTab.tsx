import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, Group, Loader, ScrollArea, Stack, Table, Text, TextInput,
} from "@mantine/core";
import { functionInfo, functionTrialRun, type FunctionInfoDto, type TrialRunDto } from "../api";

/// A function's source, its parameters, and a trial run that is rolled back. Deliberately not a
/// debugger: no stepping and no breakpoints, but for PL/pgSQL a run that shows every RAISE NOTICE
/// is how most of that debugging happens anyway.
export function FunctionTab({ connectionId, objectRef }: {
  connectionId: string;
  objectRef: string;
}) {
  const [info, setInfo] = useState<FunctionInfoDto | null>(null);
  const [args, setArgs] = useState<string[]>([]);
  const [run, setRun] = useState<TrialRunDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    functionInfo(connectionId, objectRef)
      .then(found => {
        if (cancelled) return;
        setInfo(found);
        setArgs(found.arguments.filter(a => a.mode !== "OUT").map(() => ""));
        setError(null);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setInfo(null); setRun(null); };
  }, [connectionId, objectRef]);

  if (error && !info) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!info) return <Loader size="xs" m="xs" />;

  if (!info.supported)
    return <Text size="xs" c="dimmed" p="xs">Only PostgreSQL functions can be inspected here.</Text>;

  if (!info.source)
    return <Text size="xs" c="dimmed" p="xs">No function of that name in this schema.</Text>;

  const inputs = info.arguments.filter(argument => argument.mode !== "OUT");

  const start = () => {
    setBusy(true);
    setError(null);
    // An empty box means "the default", which is not the same as an empty string — so it goes as
    // null and the function decides.
    functionTrialRun(connectionId, objectRef, args.map(value => (value === "" ? null : value)))
      .then(setRun)
      .catch(e => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="sm" p="xs">
        <Group gap="xs">
          <Badge variant="light">{info.language}</Badge>
          <Text size="xs" c="dimmed">returns {info.returns}{info.returnsSet ? " (a set)" : ""}</Text>
        </Group>

        {inputs.length > 0 && (
          <Stack gap={4}>
            <Text size="xs" fw={600}>Arguments</Text>
            {inputs.map((argument, index) => (
              <TextInput key={`${argument.name}:${index}`} size="xs"
                label={`${argument.name} — ${argument.type}`}
                placeholder={argument.hasDefault ? "leave empty for the default" : "NULL if empty"}
                value={args[index] ?? ""}
                onChange={e => setArgs(current =>
                  current.map((value, at) => (at === index ? e.currentTarget.value : value)))} />
            ))}
          </Stack>
        )}

        <Group gap="xs">
          <Button size="compact-xs" loading={busy} onClick={start}>Run and roll back</Button>
          {run && (
            <Text size="xs" c="dimmed">
              {run.elapsedMs.toFixed(1)} ms · {run.rows.length} row{run.rows.length === 1 ? "" : "s"}
              {run.truncated ? " (more were left unread)" : ""}
            </Text>
          )}
        </Group>

        <Text size="10px" c="dimmed">
          The run happens inside a transaction that is always rolled back. A sequence that moved or
          anything the function did outside the transaction still happened.
        </Text>

        {error && <Alert color="red" p="xs"><Text size="xs">{error}</Text></Alert>}

        {run && run.notices.length > 0 && (
          <Stack gap={2}>
            <Text size="xs" fw={600}>Raised</Text>
            <Code block fz="10px">{run.notices.join("\n")}</Code>
          </Stack>
        )}

        {run && run.rows.length > 0 && (
          <Table fz="xs" striped withTableBorder>
            <Table.Thead>
              <Table.Tr>{run.columns.map(column => <Table.Th key={column}>{column}</Table.Th>)}</Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {run.rows.map((row, index) => (
                <Table.Tr key={index}>
                  {row.map((value, at) => (
                    <Table.Td key={at}>
                      {value === null ? <Text size="10px" c="dimmed">NULL</Text> : String(value)}
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}

        <Stack gap={2}>
          <Text size="xs" fw={600}>Source</Text>
          <Code block fz="10px">{info.source}</Code>
        </Stack>
      </Stack>
    </ScrollArea>
  );
}
