import { useEffect, useMemo, useState } from "react";
import { Modal, ScrollArea, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { schemaCache, type TableRef } from "../editor/schemaCache";

/// Ctrl+Shift+O: type part of a name, land on the object. Reads the same cache the completion
/// uses, so it costs nothing after the first schema walk.
export function GoToObject({ connectionId, opened, onClose, onPick }: {
  connectionId: string;
  opened: boolean;
  onClose: () => void;
  onPick: (table: TableRef) => void;
}) {
  const [tables, setTables] = useState<TableRef[]>([]);
  const [search, setSearch] = useState("");
  const [cursor, setCursor] = useState(0);

  useEffect(() => {
    if (!opened || !connectionId) return;
    setSearch("");
    setCursor(0);
    schemaCache.tables(connectionId).then(setTables).catch(() => setTables([]));
  }, [opened, connectionId]);

  const matches = useMemo(() => {
    const needle = search.trim().toLowerCase();
    // Subsequence matching: "ordit" finds "order_items", the way an IDE's go-to does.
    const fuzzy = (value: string) => {
      let index = 0;
      for (const character of needle) {
        index = value.indexOf(character, index);
        if (index < 0) return false;
        index++;
      }
      return true;
    };

    return tables
      .filter(t => !needle || fuzzy(t.name.toLowerCase()) || t.schema.toLowerCase().includes(needle))
      .slice(0, 100);
  }, [tables, search]);

  const pick = (table: TableRef | undefined) => {
    if (!table) return;
    onClose();
    onPick(table);
  };

  return (
    <Modal opened={opened} onClose={onClose} withCloseButton={false} size="md" padding="xs">
      <TextInput size="sm" data-autofocus placeholder="Go to table or view" value={search}
        onChange={e => { setSearch(e.currentTarget.value); setCursor(0); }}
        onKeyDown={e => {
          if (e.key === "ArrowDown") { e.preventDefault(); setCursor(c => Math.min(c + 1, matches.length - 1)); }
          if (e.key === "ArrowUp") { e.preventDefault(); setCursor(c => Math.max(c - 1, 0)); }
          if (e.key === "Enter") { e.preventDefault(); pick(matches[cursor]); }
        }} />

      <ScrollArea h={320} mt="xs">
        <Stack gap={0}>
          {matches.length === 0
            ? <Text size="xs" c="dimmed" p="xs">Nothing matches.</Text>
            : matches.map((table, index) => (
              <UnstyledButton key={table.ref} onMouseEnter={() => setCursor(index)}
                onClick={() => pick(table)}
                style={{
                  padding: "5px 8px", borderRadius: 4,
                  background: index === cursor ? "var(--mantine-primary-color-light)" : undefined,
                }}>
                <Text size="sm" component="span">{table.name}</Text>
                <Text size="xs" c="dimmed" component="span"> · {table.schema}</Text>
              </UnstyledButton>
            ))}
        </Stack>
      </ScrollArea>
    </Modal>
  );
}
