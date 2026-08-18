import { useEffect, useRef, useState } from "react";
import { ActionIcon, Group, Switch, TextInput, Tooltip } from "@mantine/core";
import { IconCircleOff } from "@tabler/icons-react";
import { CellValue } from "../CellValue";
import type { CellState } from "./useChangeSet";

const BORDERS: Record<CellState, string | undefined> = {
  clean: undefined,
  edited: "2px solid var(--mantine-primary-color-filled)",
  inserted: "2px solid var(--mantine-color-green-6)",
  deleted: "2px solid var(--mantine-color-red-6)",
};

export function EditableCell({ value, state, editable, boolean, onCommit }: {
  value: unknown;
  state: CellState;
  editable: boolean;
  boolean: boolean;
  onCommit: (value: unknown) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => { if (editing) input.current?.focus(); }, [editing]);

  const start = () => {
    if (!editable || state === "deleted") return;
    setDraft(value === null || value === undefined ? "" : String(value));
    setEditing(true);
  };

  const commit = () => { setEditing(false); onCommit(draft); };

  if (boolean && editable && state !== "deleted") {
    return (
      <Switch size="xs" checked={value === true || value === 1 || value === "1" || value === "true"}
        onChange={e => onCommit(e.currentTarget.checked)} />
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
