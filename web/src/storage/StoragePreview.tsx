import { useEffect, useState } from "react";
import {
  Badge, Button, Code, Group, Loader, ScrollArea, Stack, Table, Text,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { IconZoomScan } from "@tabler/icons-react";
import {
  describeObject, objectUrl, previewObject,
  type ObjectDetailDto, type StoragePreviewDto,
} from "../api";
import { saveAs } from "./saveAs";
import { FileViewerModal, type ViewableFile } from "./FileViewerModal";

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
  const [saving, setSaving] = useState(false);
  const [viewing, setViewing] = useState<ViewableFile | null>(null);

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

  const type = preview.contentType ?? "";
  const image = type.startsWith("image/");
  const video = type.startsWith("video/");
  const audio = type.startsWith("audio/");
  const pdf = type === "application/pdf" || preview.key.toLowerCase().endsWith(".pdf");

  // What the server hands over without the attachment header, so the browser shows it here rather
  // than putting it in the downloads folder.
  const inline = `${objectUrl(connectionId, objectRef)}&inline=true`;

  return (
    <ScrollArea h="100%" p="xs">
      <FileViewerModal file={viewing} onClose={() => setViewing(null)} />

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
          {/* The spreadsheets, documents and archives a browser will not render by itself. */}
          <Button size="compact-xs" variant="default" leftSection={<IconZoomScan size={13} />}
            onClick={() => setViewing({
              url: inline, name: preview.name, contentType: preview.contentType,
            })}>
            View
          </Button>
          <Button size="compact-xs" variant="default"
            component="a" href={objectUrl(connectionId, objectRef)} download={preview.name}>
            Download
          </Button>
          {/* Where the browser can ask: the person picks the folder and the name, and the file is
              streamed into it rather than through memory. */}
          <Button size="compact-xs" variant="subtle" loading={saving}
            onClick={() => {
              setSaving(true);
              saveAs(objectUrl(connectionId, objectRef), preview.name,
                { contentType: preview.contentType })
                .then(outcome => {
                  if (outcome === "saved")
                    notifications.show({ message: `${preview.name} saved` });
                })
                .catch(e => notifications.show({ color: "red", message: e.message }))
                .finally(() => setSaving(false));
            }}>
            Save as…
          </Button>
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
          <img src={inline} alt={preview.name} style={{ maxWidth: "100%", borderRadius: 4 }} />}

        {/* A PDF, a video or a recording: shown where it lies rather than downloaded to be looked
            at. The bytes are the same ones the download serves. */}
        {pdf && <embed src={inline} type="application/pdf" width="100%" height={520} />}

        {video &&
          // eslint-disable-next-line jsx-a11y/media-has-caption
          <video src={inline} controls style={{ maxWidth: "100%", borderRadius: 4 }} />}

        {audio &&
          // eslint-disable-next-line jsx-a11y/media-has-caption
          <audio src={inline} controls style={{ width: "100%" }} />}

        {preview.text != null &&
          <Stack gap={2}>
            <Group gap="xs">
              <Text size="xs" fw={600}>Preview</Text>
              {preview.truncated &&
                <Text size="xs" c="dimmed">first {size(preview.text.length)} only</Text>}
            </Group>
            <Code block style={{ maxHeight: 400, overflow: "auto", whiteSpace: "pre-wrap" }}>
              {pretty(preview.text, preview.contentType, preview.truncated)}
            </Code>
          </Stack>}

        {preview.binary && !image && !pdf && !video && !audio &&
          <Text size="xs" c="dimmed">
            Nothing here reads this file. Download it, or open it where it belongs.
          </Text>}
      </Stack>
    </ScrollArea>
  );
}

/// JSON with its indentation, and everything else as it arrived.
///
/// A whole document on one line is the shape an API answers with and the shape nobody can read. Only
/// a complete document is formatted: the preview stops at a byte count, so a truncated one is not
/// JSON any more and pretending otherwise would drop the part that was read.
function pretty(text: string, contentType: string | null, truncated: boolean) {
  const looksJson = (contentType?.includes("json") ?? false)
    || text.trimStart().startsWith("{") || text.trimStart().startsWith("[");

  if (!looksJson || truncated) return text;

  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    // NDJSON, or JSON that is not: what was read is still worth showing.
    return text;
  }
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
