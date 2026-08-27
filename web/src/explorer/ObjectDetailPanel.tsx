import { useEffect, useState } from "react";
import { Badge, Group, Loader, ScrollArea, Table, Tabs, Text } from "@mantine/core";
import { describeObject, type ObjectDetailDto } from "../api";
import type { ExplorerSelection } from "./ExplorerTree";
import {
  DependenciesTab, PrivilegesTab, SqlTab, StatisticsTab,
} from "./ObjectTabs";
import { PartitionsTab, PoliciesTab } from "./ObjectAdminTabs";
import { FunctionTab } from "./FunctionTab";
import { StoragePreview } from "../storage/StoragePreview";

const DESCRIBABLE = ["Table", "View", "MaterializedView"];

/// A routine has no columns, indexes or keys to describe — but it does have a source, dependencies
/// and a run, so the panel opens on those rather than saying nothing.
const ROUTINES = ["Function", "Procedure"];

export function ObjectDetailPanel({ selection, onOpenInEditor, onOpenData }: {
  selection: ExplorerSelection | null;
  /// Opens SQL in a query tab — used by the SQL tab and by the privilege statements, which go
  /// through the editor's own preview rather than running from here.
  onOpenInEditor?: (sql: string) => void;
  /// Opens the object's rows in a data tab. A file in a bucket offers this where a reader
  /// understands it.
  onOpenData?: (selection: ExplorerSelection) => void;
}) {
  const [detail, setDetail] = useState<ObjectDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!selection || !DESCRIBABLE.includes(selection.node.kind)) return;

    let cancelled = false;
    describeObject(selection.connectionId, selection.node.ref)
      .then(d => { if (!cancelled) { setDetail(d); setError(null); } })
      .catch(e => { if (!cancelled) setError(e.message); });

    // Cleared on the way out, so a new selection never renders under the previous object's shape.
    return () => { cancelled = true; setDetail(null); setError(null); };
  }, [selection]);

  if (!selection) return <Text size="xs" c="dimmed" p="xs">Select an object.</Text>;

  // An object in a bucket has no indexes, keys or privileges. What it has is its own facts, the
  // front of its content and, where a reader understands it, the columns of the table it would be.
  if (selection.node.kind === "StorageObject")
    return (
      <StoragePreview connectionId={selection.connectionId} objectRef={selection.node.ref}
                      onOpenData={onOpenData && (() => onOpenData(selection))} />
    );

  const routine = ROUTINES.includes(selection.node.kind);

  if (!DESCRIBABLE.includes(selection.node.kind) && !routine)
    return <Text size="xs" c="dimmed" p="xs">{selection.node.label}</Text>;
  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!detail && !routine) return <Loader size="xs" m="xs" />;

  return (
    <Tabs defaultValue={routine ? "function" : "columns"} h="100%">
      <Tabs.List>
        {detail && <Tabs.Tab value="columns">Columns</Tabs.Tab>}
        {detail && <Tabs.Tab value="indexes">Indexes</Tabs.Tab>}
        {detail && <Tabs.Tab value="keys">Keys</Tabs.Tab>}
        {detail && <Tabs.Tab value="statistics">Statistics</Tabs.Tab>}
        {/* Only where they mean something: a view has no partitions, and only PostgreSQL has
            row-level security — the tabs themselves say so if opened anyway. */}
        {selection.node.kind === "Table" && <Tabs.Tab value="policies">Policies</Tabs.Tab>}
        {selection.node.kind === "Table" && <Tabs.Tab value="partitions">Partitions</Tabs.Tab>}
        {(selection.node.kind === "Function" || selection.node.kind === "Procedure") &&
          <Tabs.Tab value="function">Inspect</Tabs.Tab>}
        {detail && <Tabs.Tab value="privileges">Privileges</Tabs.Tab>}
        <Tabs.Tab value="dependencies">Dependencies</Tabs.Tab>
        <Tabs.Tab value="sql">SQL</Tabs.Tab>
        {detail && <Tabs.Tab value="info">Info</Tabs.Tab>}
      </Tabs.List>

      {/* The four tabs pgAdmin taught people to look for. Each asks the server for itself, so
          opening an object stays one request. */}
      {detail && <>
      <Tabs.Panel value="statistics" keepMounted={false}>
        <StatisticsTab connectionId={selection.connectionId} objectRef={selection.node.ref} />
      </Tabs.Panel>

      </>}

      <Tabs.Panel value="function" keepMounted={false}>
        <FunctionTab connectionId={selection.connectionId} objectRef={selection.node.ref} />
      </Tabs.Panel>

      {detail && <>
      <Tabs.Panel value="policies" keepMounted={false}>
        <PoliciesTab connectionId={selection.connectionId} objectRef={selection.node.ref}
          onScript={onOpenInEditor} />
      </Tabs.Panel>

      <Tabs.Panel value="partitions" keepMounted={false}>
        <PartitionsTab connectionId={selection.connectionId} objectRef={selection.node.ref}
          onScript={onOpenInEditor} />
      </Tabs.Panel>

      <Tabs.Panel value="privileges" keepMounted={false}>
        <PrivilegesTab connectionId={selection.connectionId} objectRef={selection.node.ref}
          onScript={onOpenInEditor} />
      </Tabs.Panel>

      </>}

      <Tabs.Panel value="dependencies" keepMounted={false}>
        <DependenciesTab connectionId={selection.connectionId} objectRef={selection.node.ref} />
      </Tabs.Panel>

      <Tabs.Panel value="sql" keepMounted={false}>
        <SqlTab connectionId={selection.connectionId} objectRef={selection.node.ref}
          onOpenInEditor={onOpenInEditor} />
      </Tabs.Panel>

      {detail && <>
      <Tabs.Panel value="columns">
        <ScrollArea h="calc(100vh - 160px)">
          <Table striped stickyHeader fz="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th><Table.Th>Type</Table.Th>
                <Table.Th>Null</Table.Th><Table.Th>Default</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {detail.columns.map(c => (
                <Table.Tr key={c.name}>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      {c.name}
                      {c.isPrimaryKey && <Badge size="xs" variant="light">PK</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>{c.dataType}</Table.Td>
                  <Table.Td>{c.nullable ? "yes" : "no"}</Table.Td>
                  <Table.Td>{c.default ?? ""}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>
      </Tabs.Panel>

      <Tabs.Panel value="indexes">
        <Table fz="xs">
          <Table.Tbody>
            {detail.indexes.map(i => (
              <Table.Tr key={i.name}>
                <Table.Td>{i.name}</Table.Td>
                <Table.Td>{i.columns.join(", ")}</Table.Td>
                <Table.Td>{i.unique ? "unique" : ""}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="keys">
        <Table fz="xs">
          <Table.Tbody>
            {detail.foreignKeys.map(f => (
              <Table.Tr key={f.name}>
                <Table.Td>{f.columns.join(", ")}</Table.Td>
                <Table.Td>&rarr; {f.referencedTable}({f.referencedColumns.join(", ")})</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="info">
        <Text size="xs" p="xs">
          Rows: {detail.rowCount ?? "unknown"}<br />
          Size: {detail.sizeBytes ? `${Math.round(detail.sizeBytes / 1024)} KiB` : "unknown"}<br />
          {detail.comment}
        </Text>
      </Tabs.Panel>
      </>}
    </Tabs>
  );
}
