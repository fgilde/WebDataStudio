import { Button, Group, Modal, ScrollArea, Tabs, Text } from "@mantine/core";

export interface CellRef { row: number; col: number; column: string; value: unknown }

function prettyJson(value: unknown): string | null {
  if (typeof value !== "string") return null;
  try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return null; }
}

// Drivers hand binary columns over as "0x…" (see AdoDriverBase.Normalize).
const hex = (value: unknown): string | null =>
  typeof value === "string" && /^0x[0-9a-f]*$/i.test(value) ? value.slice(2) : null;

function imageDataUrl(value: unknown): string | null {
  const bytes = hex(value);
  if (!bytes) return null;
  const head = bytes.slice(0, 8).toUpperCase();
  if (head.startsWith("89504E47")) return `data:image/png;base64,${base64(bytes)}`;
  if (head.startsWith("FFD8FF")) return `data:image/jpeg;base64,${base64(bytes)}`;
  if (head.startsWith("47494638")) return `data:image/gif;base64,${base64(bytes)}`;
  return null;
}

function base64(hexString: string): string {
  const bytes = new Uint8Array(hexString.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hexString.substr(i * 2, 2), 16);
  return btoa(String.fromCharCode(...bytes));
}

export function CellViewerModal({ cell, onClose }: { cell: CellRef | null; onClose: () => void }) {
  if (!cell) return null;

  const text = cell.value === null || cell.value === undefined ? "" : String(cell.value);
  const json = prettyJson(cell.value);
  const raw = hex(cell.value);
  const image = imageDataUrl(cell.value);

  const download = () => {
    const blob = new Blob([text], { type: "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${cell.column}.txt`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Modal opened onClose={onClose} title={cell.column} size="lg">
      <Tabs defaultValue={json ? "json" : image ? "image" : "text"}>
        <Tabs.List>
          <Tabs.Tab value="text">Text</Tabs.Tab>
          {json && <Tabs.Tab value="json">JSON</Tabs.Tab>}
          {raw && <Tabs.Tab value="hex">Hex</Tabs.Tab>}
          {image && <Tabs.Tab value="image">Image</Tabs.Tab>}
        </Tabs.List>

        <Tabs.Panel value="text">
          <ScrollArea h={320}>
            <Text size="xs" ff="monospace" style={{ whiteSpace: "pre-wrap" }}>{text || "NULL"}</Text>
          </ScrollArea>
        </Tabs.Panel>

        {json && (
          <Tabs.Panel value="json">
            <ScrollArea h={320}>
              <Text size="xs" ff="monospace" style={{ whiteSpace: "pre" }}>{json}</Text>
            </ScrollArea>
          </Tabs.Panel>
        )}

        {raw && (
          <Tabs.Panel value="hex">
            <ScrollArea h={320}>
              <Text size="xs" ff="monospace" style={{ wordBreak: "break-all" }}>
                {raw.replace(/(.{2})/g, "$1 ")}
              </Text>
            </ScrollArea>
          </Tabs.Panel>
        )}

        {image && (
          <Tabs.Panel value="image">
            <img src={image} alt={cell.column} style={{ maxWidth: "100%", maxHeight: 320 }} />
          </Tabs.Panel>
        )}
      </Tabs>

      <Group justify="flex-end" mt="sm">
        <Button variant="default" size="xs" onClick={download}>Download</Button>
      </Group>
    </Modal>
  );
}
