import { useCallback, useState } from "react";
import { Badge, Button, Group, Menu, ScrollArea, SegmentedControl, Tabs, Text } from "@mantine/core";
import { IconAlertTriangle, IconCopy, IconDownload, IconTable } from "@tabler/icons-react";
import { copyAsCsv, copyAsJson, copyAsMarkdown, copyAsSqlInList } from "../export/copyAs";
import { ResultGrid } from "../grid/ResultGrid";
import { RowFormView } from "../grid/RowFormView";
import type { ResultState } from "./resultStore";

export function ResultArea({ result, onExport }: {
  result: ResultState;
  onExport?: () => void;
}) {
  const [view, setView] = useState<"grid" | "form">("grid");
  const [formRow, setFormRow] = useState(0);
  const [selectedValues, setSelectedValues] = useState<unknown[]>([]);

  const onSelectionChange = useCallback((values: unknown[]) => setSelectedValues(values), []);
  const copy = (text: string) => navigator.clipboard.writeText(text);

  if (result.statements.length === 0 && result.messages.length === 0)
    return <Text size="xs" c="dimmed" p="xs">Run a statement to see results here.</Text>;

  const defaultTab = result.statements.length > 0 ? "s0" : "messages";

  return (
    <Tabs defaultValue={defaultTab} value={undefined} h="100%"
      styles={{ panel: { height: "calc(100% - 34px)", minHeight: 0 } }}>
      <Tabs.List>
        {result.statements.map(s => (
          <Tabs.Tab key={s.index} value={`s${s.index}`}
            leftSection={s.error ? <IconAlertTriangle size={13} color="var(--mantine-color-red-6)" /> : <IconTable size={13} />}>
            {result.statements.length > 1 ? `Result ${s.index + 1}` : "Result"}
          </Tabs.Tab>
        ))}
        <Tabs.Tab value="messages">
          Messages
          {result.messages.length > 0 && <Badge size="xs" ml={4} variant="light">{result.messages.length}</Badge>}
        </Tabs.Tab>
        {result.cancelled && <Tabs.Tab value="cancelled" disabled>cancelled</Tabs.Tab>}
      </Tabs.List>

      {result.statements.map(s => (
        <Tabs.Panel key={s.index} value={`s${s.index}`}>
          {s.error ? (
            <Text size="xs" c="red" p="xs" style={{ whiteSpace: "pre-wrap" }}>
              {s.error.text}
              {s.error.line !== null && <> (line {s.error.line}{s.error.column !== null && `, column ${s.error.column}`})</>}
            </Text>
          ) : s.columns.length === 0 ? (
            <Text size="xs" p="xs" c="dimmed">
              {s.rowsAffected !== null ? `${s.rowsAffected} rows affected` : "statement executed"}
              {s.elapsedMs !== null && ` · ${s.elapsedMs} ms`}
            </Text>
          ) : (
            <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
              <Group gap={6} px={4} pt={4}>
                <SegmentedControl size="xs" value={view} onChange={v => setView(v as "grid" | "form")}
                  data={[{ label: "Grid", value: "grid" }, { label: "Form", value: "form" }]} />
                {s.running && <Text size="xs" c="dimmed">running… {s.rowsRead} rows</Text>}
                <Group gap={4} ml="auto">
                  <Menu withinPortal>
                    <Menu.Target>
                      <Button size="compact-xs" variant="default" leftSection={<IconCopy size={13} />}>
                        Copy
                      </Button>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={() => copy(copyAsCsv(s.rows, s.columns))}>All rows as CSV</Menu.Item>
                      <Menu.Item onClick={() => copy(copyAsJson(s.rows, s.columns))}>All rows as JSON</Menu.Item>
                      <Menu.Item onClick={() => copy(copyAsMarkdown(s.rows, s.columns))}>All rows as Markdown</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item disabled={selectedValues.length === 0}
                        onClick={() => copy(copyAsSqlInList(selectedValues))}>
                        Selection as SQL IN-list
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                  {onExport && (
                    <Button size="compact-xs" variant="default" leftSection={<IconDownload size={13} />}
                      onClick={onExport}>
                      Export
                    </Button>
                  )}
                </Group>
              </Group>
              <div style={{ flex: 1, minHeight: 0 }}>
                {view === "grid"
                  ? <ResultGrid result={s} onSelectionChange={onSelectionChange} />
                  : <RowFormView result={s} index={formRow} onIndexChange={setFormRow} />}
              </div>
            </div>
          )}
        </Tabs.Panel>
      ))}

      <Tabs.Panel value="messages">
        <ScrollArea h="100%">
          {result.messages.length === 0
            ? <Text size="xs" c="dimmed" p="xs">No messages.</Text>
            : result.messages.map((m, i) => (
                <Text key={i} size="xs" p={4} ff="monospace">
                  <Badge size="xs" variant="light" mr={4}>{m.severity}</Badge>{m.text}
                </Text>
              ))}
        </ScrollArea>
      </Tabs.Panel>
    </Tabs>
  );
}
