import { useEffect, useRef, useState } from "react";
import { notifications } from "@mantine/notifications";
import { looksBinary, readFileAsCell, saveCell, size, toBytes } from "../binaryCell";
import { ActionIcon, Autocomplete, Group, Switch, TextInput, Tooltip } from "@mantine/core";
import { IconCircleOff, IconDownload, IconUpload } from "@tabler/icons-react";
import { CellValue } from "../CellValue";
import type { CellState } from "./useChangeSet";

const BORDERS: Record<CellState, string | undefined> = {
  clean: undefined,
  edited: "2px solid var(--mantine-primary-color-filled)",
  inserted: "2px solid var(--mantine-color-green-6)",
  deleted: "2px solid var(--mantine-color-red-6)",
};

export function EditableCell({ value, state, editable, boolean, binary, lookup, onCommit }: {
  value: unknown;
  state: CellState;
  editable: boolean;
  boolean: boolean;
  /// A column that holds bytes. Typing into one of those is not what anybody wants: it takes a file
  /// instead, and shows what is in it rather than a screen of hex.
  binary?: boolean;
  /// Present on a foreign-key column: returns the candidate values for the given search text.
  lookup?: (search: string) => Promise<{ value: unknown; label: unknown }[]>;
  onCommit: (value: unknown) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [options, setOptions] = useState<{ value: string; label: string }[]>([]);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => { if (editing) input.current?.focus(); }, [editing]);

  const pick = () => {
    const input = document.createElement("input");
    input.type = "file";

    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;

      try {
        onCommit(await readFileAsCell(file));
      } catch (e) {
        notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
      }
    };

    input.click();
  };

  const start = () => {
    if (!editable || state === "deleted") return;

    // Bytes are picked, not typed.
    if (binary || looksBinary(value)) { pick(); return; }

    setDraft(value === null || value === undefined ? "" : String(value));
    setEditing(true);
    if (lookup) search("");
  };

  const commit = () => { setEditing(false); onCommit(draft); };

  // The dropdown shows "key · label" but writes back the key: the column stores the key.
  const search = (text: string) => {
    if (!lookup) return;
    lookup(text)
      .then(items => setOptions(items.map(i => ({
        value: String(i.value ?? ""),
        label: i.label === i.value ? String(i.value ?? "") : `${i.value} · ${i.label}`,
      }))))
      .catch(() => setOptions([]));
  };

  if (boolean && editable && state !== "deleted") {
    return (
      <Switch size="xs" checked={value === true || value === 1 || value === "1" || value === "true"}
        onChange={e => onCommit(e.currentTarget.checked)} />
    );
  }

  if (editing && lookup) {
    return (
      <Group gap={2} wrap="nowrap">
        <Autocomplete size="xs" variant="unstyled" data={options} value={draft} autoFocus
          onChange={text => { setDraft(text); search(text); }}
          onOptionSubmit={option => { setEditing(false); onCommit(option); }}
          onBlur={commit}
          onKeyDown={e => {
            if (e.key === "Escape") { e.preventDefault(); setEditing(false); }
          }} />
        <Tooltip label="Set NULL">
          <ActionIcon size="xs" variant="subtle" aria-label="Set NULL"
            onMouseDown={e => { e.preventDefault(); setEditing(false); onCommit(null); }}>
            <IconCircleOff size={12} />
          </ActionIcon>
        </Tooltip>
      </Group>
    );
  }

  if (editing) {
    return (
      <Group gap={2} wrap="nowrap">
        <TextInput ref={input} size="xs" value={draft} variant="unstyled"
          onChange={e => setDraft(e.currentTarget.value)}
          onBlur={commit}
          onKeyDown={e => {
            if (e.key === "Enter") { e.preventDefault(); commit(); }
            if (e.key === "Escape") { e.preventDefault(); setEditing(false); }
          }} />
        <Tooltip label="Set NULL">
          <ActionIcon size="xs" variant="subtle" aria-label="Set NULL"
            onMouseDown={e => { e.preventDefault(); setEditing(false); onCommit(null); }}>
            <IconCircleOff size={12} />
          </ActionIcon>
        </Tooltip>
      </Group>
    );
  }

  // A blob is shown as what it is and what it weighs, with the two things anybody wants to do to
  // one: take it out, or put another one in.
  if (looksBinary(value)) {
    const bytes = toBytes(value);

    return (
      <Group gap={4} wrap="nowrap">
        <Tooltip label="Save this file">
          <ActionIcon size="xs" variant="subtle" aria-label="Save the file in this cell"
            onClick={() => saveCell("cell", value)}>
            <IconDownload size={12} />
          </ActionIcon>
        </Tooltip>
        <span style={{ fontSize: 11, opacity: 0.75 }}>{size(bytes.length)}</span>
        {editable && (
          <Tooltip label="Replace with a file">
            <ActionIcon size="xs" variant="subtle" aria-label="Replace with a file" onClick={pick}>
              <IconUpload size={12} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>
    );
  }

  return (
    <div onDoubleClick={start}
      style={{
        borderLeft: BORDERS[state],
        paddingLeft: state === "clean" ? 0 : 4,
        textDecoration: state === "deleted" ? "line-through" : undefined,
        cursor: editable ? "text" : "default",
        // Fills the cell so a double-click anywhere in it starts editing, not only on the text.
        width: "100%", minHeight: 18,
      }}>
      <CellValue value={value} />
    </div>
  );
}
