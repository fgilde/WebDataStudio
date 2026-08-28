import { useCallback, useEffect, useMemo, useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import {
  Alert, Anchor, Badge, Button, Card, Center, Group, Loader, ScrollArea, Stack, Table, Text,
  TextInput, Title,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { listReports, runReport, type ReportDto, type ReportResultDto } from "../api";

/// A saved query as a form.
///
/// Saved queries, bind parameters and shared links all existed separately; this is the shape somebody
/// who does not write SQL can use. The values live in the URL, so "the numbers for last month" is a
/// link to send rather than an explanation to give.
export function ReportPage() {
  const { id = "" } = useParams();
  const [search, setSearch] = useSearchParams();

  const [reports, setReports] = useState<ReportDto[] | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<ReportResultDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const report = useMemo(() => reports?.find(one => one.id === id) ?? null, [reports, id]);

  useEffect(() => {
    listReports().then(setReports).catch(e => setError(e.message));
  }, []);

  // The link carries the values, so opening one is the same as filling the form in.
  useEffect(() => {
    if (!report) return;

    setValues(Object.fromEntries(
      report.parameters.map(name => [name, search.get(name) ?? ""])));
  }, [report, search]);

  const run = useCallback((current: Record<string, string>) => {
    if (!report) return;

    setBusy(true);
    setError(null);

    runReport(report.id, current)
      .then(answer => { setResult(answer); setError(null); })
      .catch(e => { setResult(null); setError(e.message); })
      .finally(() => setBusy(false));
  }, [report]);

  // A link with every value in it runs by itself: that is what makes it a link worth sending.
  useEffect(() => {
    if (!report || result || busy) return;

    const filled = report.parameters.every(name => (search.get(name) ?? "").length > 0);
    if (filled) run(Object.fromEntries(report.parameters.map(n => [n, search.get(n) ?? ""])));
  }, [report, result, busy, search, run]);

  if (error && reports === null) return <Text c="red" p="md">{error}</Text>;
  if (reports === null) return <Center h="60vh"><Loader /></Center>;

  if (!report)
    return (
      <Stack p="md" gap="xs">
        <Title order={4}>Reports</Title>
        <Text size="sm" c="dimmed">
          A saved query with a connection is a report: pick one, fill in what it asks for, press run.
        </Text>
        {reports.length === 0 && (
          <Text size="sm" c="dimmed">
            None yet. Save a query with a connection — and with <code>:parameters</code> where it
            should ask something — and it appears here.
          </Text>
        )}
        {reports.map(one => (
          <Anchor key={one.id} href={`/report/${encodeURIComponent(one.id)}`} size="sm">
            {one.folder ? `${one.folder} / ` : ""}{one.name}
            {one.parameters.length > 0 && (
              <Text span size="xs" c="dimmed"> — asks for {one.parameters.join(", ")}</Text>
            )}
          </Anchor>
        ))}
      </Stack>
    );

  const link = () => {
    const params = new URLSearchParams();
    for (const [name, value] of Object.entries(values)) if (value) params.set(name, value);

    return `${window.location.origin}/report/${encodeURIComponent(report.id)}`
      + (params.size > 0 ? `?${params}` : "");
  };

  return (
    <Stack p="md" gap="sm">
      <Group gap="xs" align="baseline">
        <Title order={4}>{report.name}</Title>
        {report.folder && <Badge size="sm" variant="light">{report.folder}</Badge>}
      </Group>

      <Card withBorder padding="sm">
        <form onSubmit={e => { e.preventDefault(); setSearch(cleaned(values)); run(values); }}>
          <Stack gap="xs">
            {report.parameters.length === 0 && (
              <Text size="xs" c="dimmed">This report asks for nothing; press run.</Text>
            )}

            <Group gap="sm" align="flex-end">
              {report.parameters.map(name => (
                <TextInput key={name} size="xs" label={name} value={values[name] ?? ""}
                  onChange={e => setValues({ ...values, [name]: e.currentTarget.value })} />
              ))}

              <Button size="compact-sm" type="submit" loading={busy}>Run</Button>

              <Button size="compact-sm" variant="default"
                onClick={() => {
                  navigator.clipboard.writeText(link());
                  notifications.show({ message: "link copied — it runs by itself" });
                }}>
                Copy link
              </Button>

              {result && (
                <Button size="compact-sm" variant="subtle" onClick={() => download(report, result)}>
                  Download CSV
                </Button>
              )}
            </Group>
          </Stack>
        </form>
      </Card>

      {error && <Alert color="red" variant="light">{error}</Alert>}

      {result && (
        <>
          <Group gap="xs">
            <Text size="xs" c="dimmed">{result.rows.length} row(s)</Text>
            {result.truncated && <Badge size="xs" color="yellow">capped</Badge>}
          </Group>

          <ScrollArea h="60vh">
            <Table striped highlightOnHover fz="xs" stickyHeader>
              <Table.Thead>
                <Table.Tr>
                  {result.columns.map(column => (
                    <Table.Th key={column.name}>{column.name}</Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {result.rows.map((row, index) => (
                  <Table.Tr key={index}>
                    {row.map((cell, cellIndex) => (
                      <Table.Td key={cellIndex}>{cell === null ? "" : String(cell)}</Table.Td>
                    ))}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea>
        </>
      )}
    </Stack>
  );
}

/// Only the values that were given: an empty box in the URL reads like a value nobody typed.
function cleaned(values: Record<string, string>) {
  return Object.fromEntries(Object.entries(values).filter(([, value]) => value.length > 0));
}

function download(report: ReportDto, result: ReportResultDto) {
  const escape = (cell: unknown) => {
    const text = cell === null || cell === undefined ? "" : String(cell);
    return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };

  const csv = [
    result.columns.map(column => escape(column.name)).join(","),
    ...result.rows.map(row => row.map(escape).join(",")),
  ].join("\n");

  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `${report.name.replace(/[^\w.-]+/g, "-")}.csv`;
  link.click();
  URL.revokeObjectURL(url);
}
