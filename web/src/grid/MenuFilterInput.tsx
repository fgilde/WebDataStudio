import { useEffect, useRef, useState } from "react";
import { TextInput } from "@mantine/core";

/// A text field inside a Mantine menu, which is harder than it looks and both grids got wrong.
///
/// Three things have to be true. The input must not sit inside a `Menu.Item`: that is a button, so
/// it never hands the focus on and the menu reads every keystroke as navigation. The click and
/// keydown have to stop at this wrapper, or the menu closes itself under the typing. And a
/// debounced value has to be flushed when the menu closes — otherwise the last thing typed is
/// thrown away by the unmount, which is exactly what a user does: type, then press Escape.
///
/// `debounceMs` is for the grid that filters on the server; without it every keystroke is a round
/// trip and the page jumps back to one. The result grid filters in memory and passes 0.
export function MenuFilterInput({ value, onChange, placeholder = "Filter", debounceMs = 0 }: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  debounceMs?: number;
}) {
  const [draft, setDraft] = useState(value);

  // Refs, so the flush-on-unmount effect can stay mounted for the component's whole life. Written
  // in an effect rather than during render: a ref assignment in the render body runs on a discarded
  // render too.
  const draftRef = useRef(draft);
  const valueRef = useRef(value);
  const onChangeRef = useRef(onChange);

  useEffect(() => {
    draftRef.current = draft;
    valueRef.current = value;
    onChangeRef.current = onChange;
  }, [draft, value, onChange]);

  // The value can change from outside — cleared by "clear filter", or replaced when the menu is
  // reopened on another column.
  useEffect(() => { setDraft(value); }, [value]);

  useEffect(() => {
    if (draft === value) return;
    if (debounceMs === 0) { onChange(draft); return; }

    const timer = window.setTimeout(() => onChange(draft), debounceMs);
    return () => window.clearTimeout(timer);
  }, [draft, value, debounceMs, onChange]);

  useEffect(() => () => {
    if (draftRef.current !== valueRef.current) onChangeRef.current(draftRef.current);
  }, []);

  return (
    <div style={{ padding: "4px 8px" }}
      onClick={event => event.stopPropagation()}
      onKeyDown={event => event.stopPropagation()}>
      <TextInput size="xs" placeholder={placeholder} data-autofocus value={draft}
        onChange={event => setDraft(event.currentTarget.value)} />
    </div>
  );
}
