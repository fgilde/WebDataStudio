import { useEffect, useState } from "react";
import { Loader, Menu, ScrollArea, Text } from "@mantine/core";
import { describeObject } from "../api";

/// The columns of the table a foreign key points at, so one of them can be shown next to the id.
/// pgAdmin makes you write the join; this is the same thing without the query.
export function LookupPicker({ connectionId, targetRef, targetLabel, onPick, taken }: {
  connectionId: string;
  targetRef: string;
  targetLabel: string;
  /// Called with the column name on the other side.
  onPick: (column: string) => void;
  /// Columns already shown, so the same one is not offered twice.
  taken: string[];
}) {
  const [columns, setColumns] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    describeObject(connectionId, targetRef)
      .then(detail => { if (!cancelled) setColumns(detail.columns.map(column => column.name)); })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setColumns(null); };
  }, [connectionId, targetRef]);

  if (error) return <Text size="10px" c="red" px={8}>{error}</Text>;
  if (!columns) return <Loader size="xs" mx={8} my={4} />;

  const offered = columns.filter(column => !taken.includes(column));

  return (
    <>
      <Menu.Label>from {targetLabel}</Menu.Label>
      <ScrollArea.Autosize mah={200}>
        {offered.length === 0
          ? <Text size="10px" c="dimmed" px={8}>Every column is already shown.</Text>
          : offered.map(column => (
            <Menu.Item key={column} onClick={() => onPick(column)}>
              <Text size="xs">{column}</Text>
            </Menu.Item>
          ))}
      </ScrollArea.Autosize>
    </>
  );
}
