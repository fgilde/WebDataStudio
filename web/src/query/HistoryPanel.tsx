import { useEffect, useState } from "react";
import {
  ActionIcon, Group, Modal, ScrollArea, Text, TextInput, Tooltip, UnstyledButton,
} from "@mantine/core";
import { IconAlertTriangle, IconPhoto, IconSearch } from "@tabler/icons-react";
import { historySnapshot, listHistory, type HistoryEntryDto, type ResultSnapshot } from "../api";
import { ResultGrid } from "../grid/ResultGrid";

export function HistoryPanel({ onOpen }: { onOpen: (entry: HistoryEntryDto) => void }) {
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
