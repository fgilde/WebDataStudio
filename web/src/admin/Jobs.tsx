import { Fragment, useCallback, useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, ScrollArea, Stack, Table, Text, Tooltip,
} from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { jobHistory, jobStatement, listJobs, type JobDto, type JobRunDto, type JobsDto } from "../api";

/// What the server itself runs on a schedule: a SQL Server Agent job, a pg_cron entry, a MySQL
/// event. One list, because the question is the same — what runs, when, and did it work.
///
/// Reading is free. Changing a job is a statement handed to a query tab, like every other change in
/// this studio: nothing here enables or starts anything behind the person's back.
export function Jobs({ connectionId, onOpenInEditor }: {
  connectionId: string;
  onOpenInEditor?: (sql: string) => void;
}) {
  const [data, setData] = useState<JobsDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [history, setHistory] = useState<JobRunDto[] | null>(null);

  const load = useCallback(() => {
    setBusy(true);
    setError(null);
    listJobs(connectionId)
      .then(setData)
      .catch(e => { setData(null); setError(e.message); })
      .finally(() => setBusy(false));
  }, [connectionId]);

  useEffect(() => { load(); }, [load]);

  const show = (job: JobDto) => {
    if (open === job.id) { setOpen(null); setHistory(null); return; }

    setOpen(job.id);
    setHistory(null);
    jobHistory(connectionId, job.id).then(setHistory).catch(() => setHistory([]));
  };

  const act = (job: JobDto, action: string) =>
    jobStatement(connectionId, job.id, action)
      .then(result => onOpenInEditor?.(result.sql))
      .catch(e => setError(e.message));

  if (busy && !data) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;

  // Three distinct answers, and the difference matters: this engine has no scheduler, the scheduler
  // is there but empty, and here is what it runs.
  if (data && !data.available)
    return <Text size="xs" c="dimmed" p="xs">{data.reason}</Text>;

  return (
    <Stack gap={4} p="xs">
      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {data?.jobs.length ?? 0} in {data?.scheduler}
        </Text>
        <ActionIcon size="sm" variant="subtle" aria-label="Reload jobs" onClick={load}>
          <IconRefresh size={15} />
        </ActionIcon>
      </Group>

      {data && data.jobs.length === 0 &&
        <Text size="xs" c="dimmed">
          Nothing scheduled — or {data.scheduler} is not set up on this server.
        </Text>}

      <ScrollArea h={320}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Job</Table.Th><Table.Th>Schedule</Table.Th><Table.Th>Last run</Table.Th>
              <Table.Th>Next</Table.Th><Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data?.jobs.map(job => (
              <Fragment key={job.id}>
                <Table.Tr style={{ cursor: "pointer" }} onClick={() => show(job)}>
                  <Table.Td>
                    <Group gap={6}>
                      <Tooltip label={job.command ?? ""} multiline w={420} disabled={!job.command}>
                        <span>{job.name}</span>
                      </Tooltip>
                      {!job.enabled && <Badge size="xs" color="gray">disabled</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>{job.schedule || "—"}</Table.Td>
                  <Table.Td>
                    <Group gap={6}>
                      <span>{when(job.lastRun)}</span>
                      {job.lastOutcome &&
                        <Badge size="xs" color={colourOf(job.lastOutcome)}>{job.lastOutcome}</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>{when(job.nextRun)}</Table.Td>
                  <Table.Td>
                    <Group gap={4} justify="flex-end" onClick={event => event.stopPropagation()}>
                      {data.actions
                        // A disabled job is enabled and an enabled one disabled: the other way round
                        // is a statement that changes nothing.
                        .filter(action => action.id !== (job.enabled ? "enable" : "disable"))
                        .map(action => (
                          <Button key={action.id} size="compact-xs" variant="default"
                                  color={action.destructive ? "red" : undefined}
                                  onClick={() => act(job, action.id)}>
                            {action.label}
                          </Button>
                        ))}
                    </Group>
                  </Table.Td>
                </Table.Tr>

                {open === job.id &&
                  <Table.Tr key={`${job.id}-history`}>
                    <Table.Td colSpan={5}>
                      {history === null
                        ? <Loader size="xs" />
                        : history.length === 0
                          ? <Text size="xs" c="dimmed">No history for this job.</Text>
                          : <Table fz="xs">
                              <Table.Tbody>
                                {history.map((run, index) => (
                                  <Table.Tr key={index}>
                                    <Table.Td>{when(run.started)}</Table.Td>
                                    <Table.Td>
                                      <Badge size="xs" color={colourOf(run.outcome)}>{run.outcome}</Badge>
                                    </Table.Td>
                                    <Table.Td>
                                      {run.durationMs == null ? "—" : `${Math.round(run.durationMs / 1000)}s`}
                                    </Table.Td>
                                    <Table.Td>{run.message ?? ""}</Table.Td>
                                  </Table.Tr>
                                ))}
                              </Table.Tbody>
                            </Table>}
                    </Table.Td>
                  </Table.Tr>}
              </Fragment>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function colourOf(outcome: string) {
  if (/succeed|success|enabled/i.test(outcome)) return "green";
  if (/fail|error/i.test(outcome)) return "red";
  if (/progress|running|retry/i.test(outcome)) return "blue";
  return "gray";
}

function when(value: string | null) {
  if (!value) return "—";

  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? value : parsed.toISOString().replace("T", " ").slice(0, 16);
}
