import { useEffect, useState } from "react";
import {
  Alert, Button, Group, Modal, NumberInput, Radio, Select, Stack, Switch, Text, TextInput,
} from "@mantine/core";
import { listExportFormats, type ExportFormatDto } from "../api";

export type ExportScope = "result" | "table" | "schema";

export interface ExportTarget {
  connectionId: string;
  sql?: string;
  objectRef?: string;
  schema?: string;
  defaultName?: string;
  /// Scopes the caller can actually offer: a result tab has no table, an explorer node has no SQL.
  scopes: ExportScope[];
}

const SCOPE_LABELS: Record<ExportScope, string> = {
  result: "Whole result",
  table: "Whole table",
  schema: "Whole schema",
};

export function ExportDialog({ target, onClose }: { target: ExportTarget | null; onClose: () => void }) {
  const [formats, setFormats] = useState<ExportFormatDto[]>([]);
  const [format, setFormat] = useState("csv");
  const [scope, setScope] = useState<ExportScope>("result");
  const [delimiter, setDelimiter] = useState(",");
  const [header, setHeader] = useState(true);
  const [quoteAll, setQuoteAll] = useState(false);
  const [nullText, setNullText] = useState("");
  const [tableName, setTableName] = useState("");
  const [maxRows, setMaxRows] = useState<number | "">("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { listExportFormats().then(setFormats).catch(() => setFormats([])); }, []);

  useEffect(() => {
    if (!target) return;
    setScope(target.scopes[0]);
    setTableName(target.defaultName ?? "");
    setError(null);
  }, [target]);

  if (!target) return null;

  const selected = formats.find(f => f.format === format);
  const scopeAllowed = scope !== "schema" || (selected?.supportsSchemaScope ?? false);
  const isDelimited = format === "csv" || format === "tsv";
  const isSql = format.startsWith("sql");

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`/api/export/${format}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          connectionId: target.connectionId,
          sql: scope === "result" ? target.sql : undefined,
          objectRef: scope === "table" ? target.objectRef : undefined,
          schema: scope === "schema" ? target.schema : undefined,
          scope,
          maxRows: maxRows === "" ? undefined : maxRows,
          options: {
            delimiter, header, quoteAll, nullText,
            tableName: tableName || undefined,
          },
        }),
      });

      if (!response.ok) {
        const body = await response.text();
        let message = body;
        try { const parsed = JSON.parse(body); if (parsed?.message) message = parsed.message; } catch { /* not JSON */ }
        throw new Error(message);
      }

      // Downloading through a blob gives the browser a real progress indicator on a large export.
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = filenameFrom(response) ?? `export.${selected?.extension ?? "txt"}`;
      link.click();
      URL.revokeObjectURL(url);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title="Export" size="md">
      <Stack gap="sm">
        <Select label="Format" value={format} onChange={v => v && setFormat(v)}
          data={formats.map(f => ({ value: f.format, label: f.label }))} />

        <Radio.Group label="Scope" value={scope} onChange={v => setScope(v as ExportScope)}>
          <Group gap="md" mt={4}>
            {target.scopes.map(s => <Radio key={s} value={s} label={SCOPE_LABELS[s]} />)}
          </Group>
        </Radio.Group>

        {!scopeAllowed && (
          <Alert color="orange" p="xs">
            {selected?.label} cannot hold a whole schema. Pick SQL, Markdown or HTML, or export one table.
          </Alert>
        )}

        {isDelimited && (
          <Group grow>
            <TextInput label="Delimiter" value={delimiter} onChange={e => setDelimiter(e.currentTarget.value)} />
            <TextInput label="NULL as" value={nullText} placeholder="(empty)"
              onChange={e => setNullText(e.currentTarget.value)} />
          </Group>
        )}

        {(isDelimited || format === "html") && (
          <Switch label="Header row" checked={header} onChange={e => setHeader(e.currentTarget.checked)} />
        )}
        {isDelimited && (
          <Switch label="Quote every field" checked={quoteAll} onChange={e => setQuoteAll(e.currentTarget.checked)} />
        )}

        {(isSql || format === "xlsx") && (
          <TextInput label={format === "xlsx" ? "Sheet name" : "Table name"} value={tableName}
            onChange={e => setTableName(e.currentTarget.value)} />
        )}

        <NumberInput label="Row limit" placeholder="all rows" min={1} value={maxRows}
          onChange={v => setMaxRows(typeof v === "number" ? v : "")} />

        {error && <Text c="red" size="sm">{error}</Text>}

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button onClick={run} loading={busy} disabled={!scopeAllowed}>Export</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function filenameFrom(response: Response): string | null {
  const header = response.headers.get("content-disposition");
  return header?.match(/filename="([^"]+)"/)?.[1] ?? null;
}
