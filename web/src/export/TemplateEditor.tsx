import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Button, Code, Group, Modal, Select, Stack, Table, Text, TextInput, Textarea,
} from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import {
  deleteExportTemplate, exportTemplates, saveExportTemplate, type ExportTemplateDto,
} from "../api";

const BLANK: ExportTemplateDto = {
  id: "", label: "", extension: "txt", contentType: "text/plain",
  header: "", row: "", footer: "", separator: ", ",
};

/// An export format written here rather than shipped.
///
/// Three pieces of text with placeholders, and nothing that gets executed: DataGrip's extractors are
/// Groovy, which turns an export format into a program the studio would have to run.
export function TemplateEditor({ onClose, onSaved }: {
  onClose: () => void;
  /// The format list has to be re-read once a template is added or removed.
  onSaved?: () => void;
}) {
  const [templates, setTemplates] = useState<ExportTemplateDto[]>([]);
  const [editing, setEditing] = useState<ExportTemplateDto>(BLANK);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = () => exportTemplates()
    .then(result => { setTemplates(result.templates); setError(result.error ?? null); })
    .catch(e => setError(e.message));

  useEffect(() => { load(); }, []);

  const patch = (change: Partial<ExportTemplateDto>) =>
    setEditing(current => ({ ...current, ...change }));

  const save = () => {
    setBusy(true);
    setError(null);
    saveExportTemplate(editing)
      .then(() => { setEditing(BLANK); onSaved?.(); return load(); })
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  return (
    <Modal opened onClose={onClose} size="lg" title="Export templates">
      <Stack gap="sm">
        {error && <Alert color="yellow" variant="light">{error}</Alert>}

        {templates.length > 0 &&
          <Table fz="xs" highlightOnHover>
            <Table.Tbody>
              {templates.map(template => (
                <Table.Tr key={template.id} style={{ cursor: "pointer" }}>
                  <Table.Td onClick={() => setEditing(template)}>{template.label}</Table.Td>
                  <Table.Td onClick={() => setEditing(template)}>
                    <Text size="xs" c="dimmed">.{template.extension}</Text>
                  </Table.Td>
                  <Table.Td>
                    <ActionIcon size="sm" variant="subtle" color="red"
                                aria-label={`Delete ${template.label}`}
                                onClick={() => deleteExportTemplate(template.id)
                                  .then(() => { onSaved?.(); return load(); })
                                  .catch(e => setError(e.message))}>
                      <IconTrash size={14} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>}

        <Group grow>
          <TextInput size="xs" label="Id" value={editing.id} placeholder="jira-table"
                     onChange={event => patch({ id: event.currentTarget.value })} />
          <TextInput size="xs" label="Name" value={editing.label} placeholder="Jira table"
                     onChange={event => patch({ label: event.currentTarget.value })} />
        </Group>

        <Group grow>
          <TextInput size="xs" label="File extension" value={editing.extension}
                     onChange={event => patch({ extension: event.currentTarget.value })} />
          <Select size="xs" label="Content type" value={editing.contentType}
                  data={["text/plain", "text/csv", "application/json", "text/html", "text/markdown",
                    "application/sql"]}
                  onChange={value => patch({ contentType: value ?? "text/plain" })} />
          <TextInput size="xs" label="Value separator" value={editing.separator}
                     onChange={event => patch({ separator: event.currentTarget.value })} />
        </Group>

        <Textarea size="xs" label="Header" rows={2} value={editing.header ?? ""}
                  placeholder="-- {{table}}: {{columns}}"
                  onChange={event => patch({ header: event.currentTarget.value })} />
        <Textarea size="xs" label="Row" rows={3} value={editing.row}
                  placeholder="({{values|sql}}){{comma}}"
                  onChange={event => patch({ row: event.currentTarget.value })} />
        <Textarea size="xs" label="Footer" rows={2} value={editing.footer ?? ""}
                  onChange={event => patch({ footer: event.currentTarget.value })} />

        <Text size="xs" c="dimmed">
          Placeholders: <Code>{"{{table}}"}</Code> <Code>{"{{columns}}"}</Code>{" "}
          <Code>{"{{values}}"}</Code> <Code>{"{{index}}"}</Code> <Code>{"{{comma}}"}</Code>{" "}
          <Code>{"{{col.name}}"}</Code>. Each takes a filter:{" "}
          <Code>{"{{values|sql}}"}</Code>, <Code>json</Code>, <Code>csv</Code>, <Code>html</Code>,{" "}
          <Code>upper</Code>, <Code>lower</Code>.
        </Text>

        <Group justify="space-between">
          <Button size="compact-xs" variant="subtle" onClick={() => setEditing(BLANK)}>New</Button>
          <Group gap="xs">
            <Button size="compact-xs" variant="subtle" onClick={onClose}>Close</Button>
            <Button size="compact-xs" onClick={save} loading={busy}
                    disabled={!editing.id.trim() || !editing.row.trim()}>
              Save
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
