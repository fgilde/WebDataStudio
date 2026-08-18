import { useEffect, useState } from "react";
import { Group, ScrollArea, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconAlertTriangle, IconSearch } from "@tabler/icons-react";
import { listHistory, type HistoryEntryDto } from "../api";

export function HistoryPanel({ onOpen }: { onOpen: (entry: HistoryEntryDto) => void }) {
  const [entries, setEntries] = useState<HistoryEntryDto[]>([]);
  const [search, setSearch] = useState("");

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
            </Group>
          </UnstyledButton>
        ))}
      </ScrollArea>
    </div>
  );
}
