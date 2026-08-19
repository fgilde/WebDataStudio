import { useState } from "react";
import {
  Alert, Button, FileInput, Group, Modal, ScrollArea, Select, Stack, Switch, Table, Text, TextInput,
} from "@mantine/core";
import { IconUpload } from "@tabler/icons-react";

export interface ImportTarget { connectionId: string; table?: string }

interface Preview {
  format: string;
  columns: string[];
  sampleRows: string[][];
  detectedTypes: string[];
}

interface ImportSummary { inserted: number; failed: number; errors: string[] }

export function ImportDialog({ target, onClose, onDone }: {
  target: ImportTarget | null;
  onClose: () => void;
  onDone?: () => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [table, setTable] = useState(target?.table ?? "");
  const [hasHeader, setHasHeader] = useState(true);
  const [delimiter, setDelimiter] = useState(",");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<ImportSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!target) return null;

  const form = (extra: Record<string, string> = {}) => {
    const data = new FormData();
    if (file) data.append("file", file);
    data.append("hasHeader", String(hasHeader));
    data.append("delimiter", delimiter);
    for (const [key, value] of Object.entries(extra)) data.append(key, value);
    return data;
  };

  const call = async (url: string, body: FormData) => {
    const response = await fetch(url, { method: "POST", body });
    const text = await response.text();
    if (!response.ok) {
      let message = text;
      try { const parsed = JSON.parse(text); if (parsed?.message) message = parsed.message; } catch { /* not JSON */ }
      throw new Error(message);
    }
    return JSON.parse(text);
  };

  const loadPreview = async (chosen: File | null) => {
    setFile(chosen);
    setPreview(null);
    setSummary(null);
    setError(null);
    if (!chosen) return;

    setBusy(true);
    try {
      const data = new FormData();
      data.append("file", chosen);
      data.append("hasHeader", String(hasHeader));
      data.append("delimiter", delimiter);

      const result: Preview = await call("/api/import/preview", data);
      setPreview(result);
      // Same-name mapping is the sane default; the user only touches the exceptions.
      setMapping(Object.fromEntries(result.columns.map(c => [c, c])));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const result: ImportSummary = await call("/api/import/execute", form({
        connectionId: target.connectionId,
        table,
        mapping: JSON.stringify(mapping),
      }));
      setSummary(result);
      onDone?.();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title="Import" size="xl">
      <Stack gap="sm">
        <FileInput label="File" placeholder="CSV, Excel, JSON or SQL" value={file}
          leftSection={<IconUpload size={15} />} onChange={loadPreview} />

        <Group grow>
          <TextInput label="Target table" value={table} onChange={e => setTable(e.currentTarget.value)} />
          <TextInput label="Delimiter" value={delimiter} onChange={e => setDelimiter(e.currentTarget.value)} />
        </Group>
        <Switch label="First row is a header" checked={hasHeader}
          onChange={e => { setHasHeader(e.currentTarget.checked); loadPreview(file); }} />

        {preview && (
          <>
            <Text size="sm" fw={600}>Preview ({preview.format})</Text>
            <ScrollArea h={180}>
              <Table fz="xs" striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    {preview.columns.map((c, i) => (
                      <Table.Th key={c}>
                        {c}
                        <Text size="10px" c="dimmed">{preview.detectedTypes[i]}</Text>
                      </Table.Th>
                    ))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.sampleRows.map((row, r) => (
                    <Table.Tr key={r}>{row.map((v, c) => <Table.Td key={c}>{v}</Table.Td>)}</Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>

            <Text size="sm" fw={600}>Column mapping</Text>
            <Stack gap={4}>
              {preview.columns.map(column => (
                <Group key={column} gap="xs" wrap="nowrap">
                  <Text size="xs" w={160} truncate>{column}</Text>
                  <Text size="xs" c="dimmed">→</Text>
                  <TextInput size="xs" flex={1} placeholder="skip this column"
                    value={mapping[column] ?? ""}
                    onChange={e => { const value = e.currentTarget.value; setMapping(m => ({ ...m, [column]: value })); }} />
                </Group>
              ))}
            </Stack>
          </>
        )}

        {error && <Text c="red" size="sm">{error}</Text>}

        {summary && (
          <Alert color={summary.failed > 0 ? "orange" : "green"}>
            {summary.inserted} rows imported, {summary.failed} failed.
            {summary.errors.length > 0 && (
              <ScrollArea h={100} mt={4}>
                {summary.errors.map((e, i) => <Text key={i} size="xs" ff="monospace">{e}</Text>)}
              </ScrollArea>
            )}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Close</Button>
          <Button onClick={run} loading={busy} disabled={!file || !table}>Import</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/// Kept beside the dialog: both are about moving data in, and neither is big enough for its own file.
export function CopyTableDialog({ source, connections, onClose }: {
  source: { connectionId: string; objectRef: string; label: string } | null;
  connections: { id: string; name: string }[];
  onClose: () => void;
}) {
  const [targetConnection, setTargetConnection] = useState<string | null>(null);
  const [targetTable, setTargetTable] = useState(source?.label ?? "");
  const [summary, setSummary] = useState<ImportSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!source) return null;

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch("/api/copy-table", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          sourceConnectionId: source.connectionId,
          sourceRef: source.objectRef,
          targetConnectionId: targetConnection,
          targetTable,
        }),
      });

      const text = await response.text();
      if (!response.ok) {
        let message = text;
        try { const parsed = JSON.parse(text); if (parsed?.message) message = parsed.message; } catch { /* not JSON */ }
        throw new Error(message);
      }
      setSummary(JSON.parse(text));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={`Copy ${source.label}`} size="md">
      <Stack gap="sm">
        <Select label="Target connection" value={targetConnection} onChange={setTargetConnection}
          data={connections.map(c => ({ value: c.id, label: c.name }))} />
        <TextInput label="Target table" value={targetTable}
          onChange={e => setTargetTable(e.currentTarget.value)}
          description="The table must already exist on the target" />

        {error && <Text c="red" size="sm">{error}</Text>}
        {summary && (
          <Alert color={summary.failed > 0 ? "orange" : "green"}>
            {summary.inserted} rows copied, {summary.failed} failed.
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Close</Button>
          <Button onClick={run} loading={busy} disabled={!targetConnection || !targetTable}>Copy</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
