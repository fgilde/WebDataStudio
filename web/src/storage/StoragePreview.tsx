import { useEffect, useState } from "react";
import {
  Anchor, Badge, Button, Code, Group, Loader, ScrollArea, Stack, Table, Text,
} from "@mantine/core";
import {
  describeObject, objectUrl, previewObject,
  type ObjectDetailDto, type StoragePreviewDto,
} from "../api";

/// What one object in a bucket is: its own facts, the front of its content, and — where a reader
/// understands it — the columns it would have as a table.
///
/// The preview never reads a whole file. A 4 GB Parquet clicked on by accident must cost a page, not
/// a download.
export function StoragePreview({ connectionId, objectRef, onOpenData }: {
  connectionId: string;
  objectRef: string;
  onOpenData?: () => void;
}) {
  const [preview, setPreview] = useState<StoragePreviewDto | null>(null);
  const [detail, setDetail] = useState<ObjectDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    previewObject(connectionId, objectRef)
      .then(p => {
        if (cancelled) return;
        setPreview(p);
        setError(null);
        // The column list is worth a second call only for something that reads as a table.
        if (p.queryable)
          describeObject(connectionId, objectRef)
            .then(d => { if (!cancelled) setDetail(d); })
            .catch(() => { /* the preview stands on its own */ });
      })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; setPreview(null); setDetail(null); setError(null); };
  }, [connectionId, objectRef]);

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!preview) return <Loader size="xs" m="xs" />;

  const image = preview.contentType?.startsWith("image/") ?? false;

  return (
    <ScrollArea h="100%" p="xs">
      <Stack gap="xs">
        <Group gap="xs">
          <Text size="sm" fw={600}>{preview.name}</Text>
          <Badge size="xs" variant="light">{size(preview.size)}</Badge>
          {preview.contentType && <Badge size="xs" variant="light">{preview.contentType}</Badge>}
          {preview.storageClass && <Badge size="xs" variant="light">{preview.storageClass}</Badge>}
        </Group>

        <Group gap="xs">
          {preview.queryable && onOpenData &&
            <Button size="compact-xs" variant="light" onClick={onOpenData}>Open data</Button>}
          <Anchor size="xs" href={objectUrl(connectionId, objectRef)} download={preview.name}>
            Download
          </Anchor>
        </Group>

        <Table withRowBorders={false} verticalSpacing={2}>
          <Table.Tbody>
            <Fact label="Key" value={preview.key} />
            <Fact label="Modified" value={when(preview.modified)} />
            <Fact label="ETag" value={preview.etag ?? "—"} />
            {detail?.rowCount != null && <Fact label="Rows" value={String(detail.rowCount)} />}
          </Table.Tbody>
        </Table>

        {detail && detail.columns.length > 0 &&
          <Stack gap={2}>
            <Text size="xs" fw={600}>Columns</Text>
            <Table withRowBorders={false} verticalSpacing={2}>
              <Table.Tbody>
                {detail.columns.map(column => (
                  <Table.Tr key={column.name}>
                    <Table.Td><Text size="xs">{column.name}</Text></Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{column.dataType}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Stack>}

        {image &&
          <img src={objectUrl(connectionId, objectRef)} alt={preview.name}
               style={{ maxWidth: "100%", borderRadius: 4 }} />}

        {preview.text != null &&
          <Stack gap={2}>
            <Group gap="xs">
              <Text size="xs" fw={600}>Preview</Text>
              {preview.truncated &&
                <Text size="xs" c="dimmed">first {size(preview.text.length)} only</Text>}
            </Group>
            <Code block style={{ maxHeight: 400, overflow: "auto", whiteSpace: "pre-wrap" }}>
              {preview.text}
            </Code>
          </Stack>}

        {preview.binary && !image &&
          <Text size="xs" c="dimmed">
            Nothing here reads this file. Download it, or open it where it belongs.
          </Text>}
      </Stack>
    </ScrollArea>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <Table.Tr>
      <Table.Td w={90}><Text size="xs" c="dimmed">{label}</Text></Table.Td>
      <Table.Td><Text size="xs" style={{ wordBreak: "break-all" }}>{value}</Text></Table.Td>
    </Table.Tr>
  );
}

/// A timestamp a person reads. The wire carries full precision and an offset, which is right for a
/// machine and noise in a detail line.
function when(value: string | null) {
  if (!value) return "—";

  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? value : parsed.toISOString().replace("T", " ").slice(0, 19);
}

function size(bytes: number) {
  const units = ["B", "kB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return unit === 0 ? `${bytes} B` : `${value.toFixed(1)} ${units[unit]}`;
}
