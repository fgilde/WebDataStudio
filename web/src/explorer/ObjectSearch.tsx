import { useEffect, useState } from "react";
import { Group, Loader, Text, UnstyledButton } from "@mantine/core";
import { schemaCache, type TableRef } from "../editor/schemaCache";
import { fuzzyRank } from "./fuzzy";
import { nodeIcon } from "./nodeIcons";
import type { Connection, SchemaNodeDto } from "../api";
import type { ExplorerSelection } from "./ExplorerTree";

/// What the filter box does once somebody types into it: a flat list of the tables and views that
/// match, across every connection that is expanded.
///
/// The tree's own filter compared the labels of the first level, which is schemas on PostgreSQL,
/// folders on SQLite and database indexes on Redis — never tables. Typing a table name therefore
/// emptied the tree. What people look for is an object, so that is what this searches, and the
/// schema it lives in travels with the row as context instead of being filtered away.
export function ObjectSearch({ connections, filter, onSelect, onAction, onContextMenu }: {
  connections: Connection[];
  filter: string;
  onSelect: (selection: ExplorerSelection) => void;
  onAction: (action: "open-data", selection: ExplorerSelection) => void;
  /// A search result is a real object, so it carries the same menu a tree row does.
  onContextMenu: (selection: ExplorerSelection, x: number, y: number) => void;
}) {
  const [byConnection, setByConnection] = useState<Record<string, TableRef[]>>({});
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);

    // One walk per connection, cached: the same cache the editor's completion and "go to object"
    // already use, so a second search costs nothing.
    Promise.all(connections.map(async connection => ({
      id: connection.id,
      tables: await schemaCache.tables(connection.id).catch(() => []),
    })))
      .then(results => {
        if (cancelled) return;
        setByConnection(Object.fromEntries(results.map(r => [r.id, r.tables])));
        setLoading(false);
      });

    return () => { cancelled = true; };
  }, [connections]);

  const rows = connections.flatMap(connection =>
    fuzzyRank(byConnection[connection.id] ?? [], filter, table => table.name, 50)
      .map(table => ({ connection, table })));

  if (loading && rows.length === 0)
    return <Group gap={6} p="xs"><Loader size="xs" /><Text size="xs" c="dimmed">Searching…</Text></Group>;

  if (rows.length === 0)
    return <Text size="xs" c="dimmed" p="xs">Nothing matches “{filter}”.</Text>;

  return (
    <>
      {rows.map(({ connection, table }) => {
        const node: SchemaNodeDto = {
          ref: table.ref, kind: table.ref.startsWith("View:") ? "View" : "Table",
          label: table.name, hasChildren: true, detail: null,
        };
        const selection = { connectionId: connection.id, node };

        return (
          <UnstyledButton key={`${connection.id}:${table.ref}`} w="100%" px={6} py={3}
            onClick={() => onSelect(selection)}
            onDoubleClick={() => onAction("open-data", selection)}
            onContextMenu={event => {
              event.preventDefault();
              onSelect(selection);
              onContextMenu(selection, event.clientX, event.clientY);
            }}>
            <Group gap={6} wrap="nowrap">
              {nodeIcon(node.kind)}
              <Text size="xs" truncate>{table.name}</Text>
              {/* The context the tree would have given: which schema, and on which connection. */}
              <Text size="10px" c="dimmed" truncate ml="auto">
                {[connection.name, table.schema].filter(Boolean).join(" · ")}
              </Text>
            </Group>
          </UnstyledButton>
        );
      })}
    </>
  );
}
