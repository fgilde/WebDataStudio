import { Alert, Badge, Group, Loader, Modal, ScrollArea, Table, Text } from "@mantine/core";
import { useEffect, useState } from "react";
import { rowHistory, type RowHistoryDto } from "../api";
import { CellValue } from "../grid/CellValue";

/// What one row looked like before it looked like this.
///
/// Only where the database kept the answer itself — a system-versioned table on SQL Server, system
/// versioning on MariaDB, Oracle's undo. The studio's own audit trail is a different thing: it only
/// knows what went through the studio, and the row somebody changed from an application is exactly
/// the one being asked about.
export function RowHistoryModal({ connectionId, objectRef, keyValues, label, onClose }: {
  connectionId: string;
  objectRef: string;
  /// The columns that address this one row, as `column: value`.
  keyValues: Record<string, string> | null;
  label: string;
  onClose: () => void;
}) {
  const [history, setHistory] = useState<RowHistoryDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!keyValues) return;

    let cancelled = false;
    setHistory(null);
    setError(null);

    rowHistory(connectionId, objectRef, keyValues)
      .then(answer => { if (!cancelled) setHistory(answer); })
      .catch(e => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      });

    return () => { cancelled = true; };
  }, [connectionId, objectRef, keyValues]);

  return (
    <Modal opened={keyValues !== null} onClose={onClose} size="xl" title={`History of ${label}`}>
      {error && <Alert color="red" p={8}><Text size="xs">{error}</Text></Alert>}
      {!error && !history && <Loader size="xs" />}

      {history?.note && (
        <Alert color="gray" variant="light" p={8} mb="xs">
          <Text size="xs">{history.note}</Text>
        </Alert>
      )}

      {history && history.versions.length === 0 && !history.note && (
        <Text size="xs" c="dimmed">The database kept no earlier version of this row.</Text>
      )}

      {history && history.versions.length > 0 && (
        <ScrollArea h={420}>
          <Table fz="xs" striped withColumnBorders stickyHeader>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>From</Table.Th>
                <Table.Th>To</Table.Th>
                {history.columns.map(column => (
                  <Table.Th key={column.name}>{column.name}</Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {history.versions.map((version, index) => (
                <Table.Tr key={index}>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      {index === 0 && <Badge size="xs" variant="light">now</Badge>}
                      <Text size="xs">{version.from ?? ""}</Text>
                    </Group>
                  </Table.Td>
                  <Table.Td>{version.to ?? ""}</Table.Td>
                  {history.columns.map((column, position) => (
                    <Table.Td key={column.name}
                      // What moved between this version and the one before it — the reason to read
                      // a list of near-identical rows at all.
                      style={version.changed.includes(column.name)
                        ? { background: "var(--mantine-primary-color-light)" }
                        : undefined}>
                      <CellValue value={version.values[position]} />
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>
      )}
    </Modal>
  );
}
