import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Modal, ScrollArea, Stack, Text, TextInput,
  Tooltip, UnstyledButton,
} from "@mantine/core";
import { IconRefresh, IconTrash } from "@tabler/icons-react";
import {
  archiveInsertScript, deleteArchive, listArchives, readArchive,
  type ArchiveInfoDto, type ArchiveListDto, type ArchivePageDto,
} from "../api";
import { ResultGrid } from "../grid/ResultGrid";
import { formatBytes } from "../redis/format";

/// Results kept as files: what a table looked like before the migration, what the report said last
/// Tuesday. DbGate calls them archives, and they answer that without a second database to put them
/// in.
export function ArchivePanel({ connectionId, onScript }: {
  /// Where an "insert these rows" script would go. Only used for that.
  connectionId: string;
  onScript?: (sql: string) => void;
}) {
  const [list, setList] = useState<ArchiveListDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [chosen, setChosen] = useState<ArchiveInfoDto | null>(null);
  const [page, setPage] = useState<ArchivePageDto | null>(null);
  const [target, setTarget] = useState<string | null>(null);
  const [table, setTable] = useState("");
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let cancelled = false;

    listArchives()
      .then(found => { if (!cancelled) { setList(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; };
  }, [nonce]);

  useEffect(() => {
    if (!chosen) return;
    let cancelled = false;

    readArchive(chosen.name)
      .then(found => { if (!cancelled) { setPage(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setPage(null); };
  }, [chosen]);

  const remove = (name: string) => {
    deleteArchive(name)
      .then(() => { setChosen(current => (current?.name === name ? null : current)); setNonce(n => n + 1); })
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  };

  const script = () => {
    if (!chosen || !table.trim()) return;

    archiveInsertScript(chosen.name, connectionId, table.trim())
      .then(built => { onScript?.(built.sql); setTarget(null); })
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  };

  if (!list) return <Loader size="xs" m="xs" />;

  return (
    <Group align="stretch" gap={0} h="100%" style={{ minHeight: 0 }}>
      <Stack gap={2} w={260} p={4} style={{ borderRight: "1px solid var(--mantine-color-default-border)" }}>
        <Group justify="space-between">
          <Text size="xs" fw={600}>Archives</Text>
          <Tooltip label="Reload">
            <ActionIcon size="sm" variant="subtle" aria-label="Reload"
              onClick={() => setNonce(n => n + 1)}>
              <IconRefresh size={13} />
            </ActionIcon>
          </Tooltip>
        </Group>

        {!list.available && (
          <Alert color="orange" p={6}>
            <Text size="10px">{list.error ?? "the archive directory is not usable"}</Text>
          </Alert>
        )}

        <Text size="9px" c="dimmed" lineClamp={1}>{list.path}</Text>

        <ScrollArea style={{ flex: 1 }}>
          {list.items.length === 0 && (
            <Text size="10px" c="dimmed" p={4}>
              Nothing kept yet. "Keep as archive" on a result puts one here.
            </Text>
          )}

          {list.items.map(item => (
            <Group key={item.name} gap={2} wrap="nowrap"
              style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
              <UnstyledButton style={{ flex: 1, padding: "4px 2px" }} onClick={() => setChosen(item)}>
                <Text size="xs" fw={chosen?.name === item.name ? 700 : 400} lineClamp={1}>
                  {item.name}
                </Text>
                <Group gap={5}>
                  <Text size="9px" c="dimmed">{item.rows} rows</Text>
                  <Text size="9px" c="dimmed">{formatBytes(item.sizeBytes)}</Text>
                  <Text size="9px" c="dimmed">{new Date(item.savedAt).toLocaleString()}</Text>
                </Group>
              </UnstyledButton>
              <Tooltip label="Delete this archive">
                <ActionIcon size="sm" variant="subtle" color="red" aria-label={`Delete ${item.name}`}
                  onClick={() => remove(item.name)}>
                  <IconTrash size={12} />
                </ActionIcon>
              </Tooltip>
            </Group>
          ))}
        </ScrollArea>
      </Stack>

      <Stack gap={4} p={4} style={{ flex: 1, minHeight: 0 }}>
        {error && <Alert color="red" p={6}><Text size="xs">{error}</Text></Alert>}

        {!chosen && <Text size="xs" c="dimmed">Pick an archive.</Text>}

        {chosen && (
          <>
            <Group gap="xs">
              <Text size="xs" fw={600}>{chosen.name}</Text>
              <Badge size="xs" variant="light">{chosen.rows} rows</Badge>
              {chosen.source && (
                <Text size="10px" c="dimmed" lineClamp={1}>from {chosen.source}</Text>
              )}
              {onScript && (
                <Button size="compact-xs" variant="default" onClick={() => setTarget(chosen.name)}>
                  Script the rows as INSERTs…
                </Button>
              )}
            </Group>

            {page && page.total > page.rows.length && (
              <Text size="9px" c="dimmed">
                the first {page.rows.length} of {page.total}; the file holds all of them
              </Text>
            )}

            <div style={{ flex: 1, minHeight: 0 }}>
              {page
                ? (
                  <ResultGrid result={{
                    index: 0,
                    columns: page.columns.map(column => ({
                      name: column.name, dataType: column.dataType, nullable: true,
                    })),
                    rows: page.rows,
                    documents: [],
                    rowsAffected: null,
                    elapsedMs: null,
                    rowsRead: page.rows.length,
                    truncated: page.total > page.rows.length,
                    error: null,
                    running: false,
                  }} />
                )
                : <Loader size="xs" />}
            </div>
          </>
        )}
      </Stack>

      <Modal opened={target !== null} onClose={() => setTarget(null)} title="Insert these rows into"
        size="md">
        <Stack gap="sm">
          <TextInput size="xs" label="Table" placeholder="public.customers_restored" data-autofocus
            value={table} onChange={event => setTable(event.currentTarget.value)} />
          <Text size="xs" c="dimmed">
            The statements open in a query tab. Nothing runs until you run them there.
          </Text>
          <Group justify="flex-end" gap="xs">
            <Button size="xs" variant="default" onClick={() => setTarget(null)}>Cancel</Button>
            <Button size="xs" disabled={!table.trim()} onClick={script}>Build the script</Button>
          </Group>
        </Stack>
      </Modal>
    </Group>
  );
}
