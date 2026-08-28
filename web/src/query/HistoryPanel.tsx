import { useEffect, useState } from "react";
import {
  ActionIcon, Group, Modal, ScrollArea, Tabs, Text, TextInput, Tooltip, UnstyledButton,
} from "@mantine/core";
import { IconAlertTriangle, IconPhoto, IconSearch } from "@tabler/icons-react";
import { historySnapshot, listHistory, type HistoryEntryDto, type ResultSnapshot } from "../api";
import { ResultGrid } from "../grid/ResultGrid";
import { StatementStatsPanel } from "./StatementStatsPanel";

export function HistoryPanel({ onOpen, connectionId, onOpenSql }: {
  onOpen: (entry: HistoryEntryDto) => void;
  /// Which connection the statistics are about. Absent means every connection.
  connectionId?: string;
  /// Opens one of the statements in a query tab.
  onOpenSql?: (sql: string) => void;
}) {
  const [entries, setEntries] = useState<HistoryEntryDto[]>([]);
  const [search, setSearch] = useState("");
  const [shown, setShown] = useState<{ entry: HistoryEntryDto; snapshot: ResultSnapshot } | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const openSnapshot = (entry: HistoryEntryDto) => {
    setFailure(null);
    historySnapshot(entry.id)
      .then(snapshot => setShown({ entry, snapshot }))
      .catch(e => setFailure(e instanceof Error ? e.message : String(e)));
  };

  useEffect(() => {
    const timer = setTimeout(() => {
      listHistory({ search: search || undefined, limit: 200 }).then(setEntries).catch(() => setEntries([]));
    }, 200);
    return () => clearTimeout(timer);
  }, [search]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      {/* The runs themselves, and what they add up to: where the time goes and what got slower. */}
      <Tabs defaultValue="runs" keepMounted={false} style={{ display: "flex", flexDirection: "column",
        height: "100%", minHeight: 0 }}>
        <Tabs.List>
          <Tabs.Tab value="runs">Runs</Tabs.Tab>
          <Tabs.Tab value="statistics">Statistics</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="statistics" style={{ flex: 1, minHeight: 0 }}>
          <StatementStatsPanel connectionId={connectionId} onOpen={onOpenSql} />
        </Tabs.Panel>

        <Tabs.Panel value="runs" style={{ flex: 1, minHeight: 0, display: "flex",
          flexDirection: "column" }}>
      <TextInput size="xs" m={4} placeholder="Search history" leftSection={<IconSearch size={13} />}
        value={search} onChange={e => setSearch(e.currentTarget.value)} />

      <ScrollArea style={{ flex: 1 }}>
        {entries.length === 0 && <Text size="xs" c="dimmed" p="xs">Nothing yet.</Text>}
        {entries.map(e => (
          <UnstyledButton key={e.id} w="100%" px={6} py={4} onClick={() => onOpen(e)}
            style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
            <Text size="xs" ff="monospace" lineClamp={2}>{e.sql}</Text>
            <Group gap={6} mt={2}>
              {e.error && <IconAlertTriangle size={11} color="var(--mantine-color-red-6)" />}
              <Text size="10px" c="dimmed">{new Date(e.executedAt).toLocaleString()}</Text>
              {e.elapsedMs !== null && <Text size="10px" c="dimmed">{e.elapsedMs} ms</Text>}
              {e.rowCount !== null && <Text size="10px" c="dimmed">{e.rowCount} rows</Text>}
              {/* The kept result, if this run kept one. Clicking it opens what came back then
                  rather than running the statement again. */}
              {e.hasSnapshot && (
                <Tooltip label="Open the result this run returned">
                  <ActionIcon size="xs" variant="subtle" aria-label="Open kept result"
                    component="div" role="button"
                    onClick={event => { event.stopPropagation(); openSnapshot(e); }}>
                    <IconPhoto size={11} />
                  </ActionIcon>
                </Tooltip>
              )}
            </Group>
          </UnstyledButton>
        ))}
        {failure && <Text size="10px" c="red" p="xs">{failure}</Text>}
      </ScrollArea>
        </Tabs.Panel>
      </Tabs>

      <Modal opened={shown !== null} onClose={() => setShown(null)} size="90%"
        title={`Result kept ${shown ? new Date(shown.entry.executedAt).toLocaleString() : ""}`}>
        {shown && (
          <div style={{ height: "60vh" }}>
            <ResultGrid result={{
              index: 0,
              columns: shown.snapshot.columns.map(name => ({ name, dataType: "", nullable: true })),
              rows: shown.snapshot.rows,
              documents: [],
              rowsAffected: null,
              elapsedMs: shown.entry.elapsedMs,
              rowsRead: shown.snapshot.rows.length,
              truncated: shown.snapshot.truncated,
              error: null,
              running: false,
            }} />
          </div>
        )}
      </Modal>
    </div>
  );
}
