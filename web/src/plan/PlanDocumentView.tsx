import { useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Menu, ScrollArea, Select, Stack, Text, TextInput, Tooltip,
} from "@mantine/core";
import {
  IconAlertTriangle, IconChevronDown, IconCopy, IconDownload, IconPlayerPlay, IconSearch,
} from "@tabler/icons-react";
import type { PlanDocumentDto, PlanNodeDto } from "../api";
import { PlanGraph } from "./PlanGraph";
import { PlanProperties } from "./PlanProperties";
import { flattenPlan, savePlanFile } from "./planModel";

const statementLabel = (text: string | null, i: number) =>
  `${i + 1}. ${(text ?? "statement").replace(/\s+/g, " ").trim().slice(0, 80)}`;

export function PlanDocumentView({ document, name, onRunStatement }: {
  document: PlanDocumentDto; name: string; onRunStatement?: (statement: string) => void;
}) {
  const withPlans = document.statements.filter(s => s.root);
  const [index, setIndex] = useState(0);
  const statement = withPlans[Math.min(index, withPlans.length - 1)];
  const [selected, setSelected] = useState<{ id: string; node: PlanNodeDto } | null>(null);
  const [search, setSearch] = useState("");
  const [cursor, setCursor] = useState(0);
  const [notesOpen, setNotesOpen] = useState(false);

  const q = search.trim().toLowerCase();
  const matches = q && statement?.root
    ? flattenPlan(statement.root)
      .filter(f => f.node.operation.toLowerCase().includes(q) || (f.node.object ?? "").toLowerCase().includes(q))
      .map(f => f.id)
    : [];
  const matchSet = new Set(matches);

  if (!statement?.root) return <Text size="xs" c="dimmed" p="xs">This plan has no statement with an operator tree.</Text>;

  // A plan repeats a warning for every column it converts; once each is what anybody reads.
  const warnings = [...new Set(statement.warnings)];
  const noteCount = warnings.length + statement.missingIndexes.length;
  // A few notes stay in view; a long list folds behind its count so the graph keeps the room.
  const showNotes = noteCount <= 3 || notesOpen;

  const header = statement.properties.filter(p =>
    ["Degree Of Parallelism", "Memory Grant", "Compile Time", "Statement Optm Level", "Cardinality Estimation Model Version"]
      .includes(p.name));

  return (
    <Stack gap={4} h="100%" style={{ minHeight: 0 }}>
      <Group gap={6} px={6} wrap="nowrap">
        {withPlans.length > 1 && (
          <Select size="xs" w={320} aria-label="Statement" allowDeselect={false}
            data={withPlans.map((s, i) => ({ value: String(i), label: statementLabel(s.text, i) }))}
            value={String(index)} onChange={v => { setIndex(Number(v)); setSelected(null); }} />
        )}
        {statement.cost != null && <Badge size="sm" variant="light" tt="none">cost {statement.cost.toFixed(3)}</Badge>}
        {header.map(p => <Text key={p.name} size="10px" c="dimmed">{p.name} {p.value}</Text>)}
        <div style={{ flex: 1 }} />
        <TextInput size="xs" w={200} placeholder="Find operator or object" leftSection={<IconSearch size={12} />}
          value={search} onChange={e => { setSearch(e.currentTarget.value); setCursor(0); }}
          onKeyDown={e => { if (e.key === "Enter" && matches.length) setCursor(c => (c + 1) % matches.length); }}
          rightSection={matches.length ? <Text size="10px" c="dimmed">{cursor + 1}/{matches.length}</Text> : null}
          rightSectionWidth={44} />
        {document.rawFormat === "sqlplan" && document.raw && (
          <Menu position="bottom-end">
            <Menu.Target>
              <Button size="compact-xs" variant="default" leftSection={<IconDownload size={12} />}
                rightSection={<IconChevronDown size={10} />}>Save</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item onClick={() => savePlanFile(document.raw!, name, "sqlplan")}>Save as .sqlplan</Menu.Item>
              <Menu.Item onClick={() => savePlanFile(document.raw!, name, "xml")}>Save as .xml</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
      </Group>

      {noteCount > 3 && (
        <Button size="compact-xs" variant="subtle" color="orange" mx={6} style={{ alignSelf: "flex-start" }}
          leftSection={<IconAlertTriangle size={12} />} rightSection={<IconChevronDown size={10} />}
          onClick={() => setNotesOpen(o => !o)}>
          {[warnings.length > 0 && `${warnings.length} warning${warnings.length === 1 ? "" : "s"}`,
            statement.missingIndexes.length > 0 && `${statement.missingIndexes.length} missing index${statement.missingIndexes.length === 1 ? "" : "es"}`]
            .filter(Boolean).join(" · ")}
        </Button>
      )}
      {showNotes && noteCount > 0 && (
        <ScrollArea.Autosize mah={180} px={6}>
          {warnings.map((w, i) => <Text key={i} size="xs" c="orange">⚠ {w}</Text>)}
          {statement.missingIndexes.map((ddl, i) => (
            <Alert key={i} variant="light" color="teal" p={4}>
              <Group gap={4} wrap="nowrap">
                <Text size="xs" ff="monospace" style={{ flex: 1, whiteSpace: "pre-wrap" }}>{ddl}</Text>
                <Tooltip label="Copy">
                  <ActionIcon size="xs" variant="subtle" aria-label="Copy index" onClick={() => navigator.clipboard.writeText(ddl)}>
                    <IconCopy size={12} />
                  </ActionIcon>
                </Tooltip>
                {onRunStatement && (
                  <Tooltip label="Open in a new query tab">
                    <ActionIcon size="xs" variant="subtle" aria-label="Open index in a query tab" onClick={() => onRunStatement(ddl)}>
                      <IconPlayerPlay size={12} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            </Alert>
          ))}
        </ScrollArea.Autosize>
      )}

      <div style={{ flex: 1, minHeight: 0, display: "flex" }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <PlanGraph root={statement.root} statementCost={statement.cost} selected={selected?.id ?? null}
            onSelect={(id, node) => setSelected(id && node ? { id, node } : null)}
            matches={matchSet} focus={matches[cursor] ?? null} />
        </div>
        <div style={{ width: 340, borderLeft: "1px solid var(--mantine-color-default-border)", minHeight: 0 }}>
          <PlanProperties
            title={selected ? `${selected.node.operation}${selected.node.nodeId != null ? ` (Node ${selected.node.nodeId})` : ""}` : "Statement"}
            properties={selected ? selected.node.properties ?? [] : statement.properties} />
        </div>
      </div>
    </Stack>
  );
}
