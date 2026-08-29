import { useCallback, useEffect, useRef, useState } from "react";

/// Two panes above each other with a handle between them.
///
/// The query tab had a fixed half-and-half: a long statement scrolled inside four lines while the
/// result had room to spare, and there was no way to say otherwise. The ratio is kept per name in
/// the browser, so the editor you made tall stays tall the next morning.
///
/// Built rather than installed: a drag, a clamp and a stored number is less code than the smallest
/// splitter package, and one fewer thing to keep current.
export function SplitPane({ id, top, bottom, minTop = 80, minBottom = 80, initial = 0.5 }: {
  /// What the stored ratio is called. One per place a splitter appears.
  id: string;
  top: React.ReactNode;
  bottom: React.ReactNode;
  /// Pixels each side keeps, so a pane can never be dragged to nothing.
  minTop?: number;
  minBottom?: number;
  initial?: number;
}) {
  const key = `wds.split.${id}`;
  const host = useRef<HTMLDivElement>(null);

  const [ratio, setRatio] = useState(() => {
    try {
      const stored = Number(localStorage.getItem(key));
      return Number.isFinite(stored) && stored > 0.05 && stored < 0.95 ? stored : initial;
    } catch {
      return initial;
    }
  });

  const [dragging, setDragging] = useState(false);

  const move = useCallback((clientY: number) => {
    const box = host.current?.getBoundingClientRect();
    if (!box || box.height <= 0) return;

    // Clamped in pixels rather than in ratio: "at least eighty pixels" means the same thing in a
    // tall window and a short one, and a ratio does not.
    const wanted = clientY - box.top;
    const lowest = minTop;
    const highest = box.height - minBottom;
    if (highest <= lowest) return;

    setRatio(Math.min(highest, Math.max(lowest, wanted)) / box.height);
  }, [minTop, minBottom]);

  useEffect(() => {
    if (!dragging) return;

    const onMove = (event: MouseEvent) => move(event.clientY);
    const onUp = () => setDragging(false);

    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);

    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [dragging, move]);

  // Written when the drag ends rather than on every pixel of it.
  useEffect(() => {
    if (dragging) return;
    try { localStorage.setItem(key, String(ratio)); } catch { /* site data is off; still works */ }
  }, [dragging, ratio, key]);

  const nudge = (event: React.KeyboardEvent) => {
    if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;

    const box = host.current?.getBoundingClientRect();
    if (!box) return;

    event.preventDefault();
    move(box.top + box.height * ratio + (event.key === "ArrowDown" ? 24 : -24));
  };

  return (
    <div ref={host} style={{
      flex: 1, minHeight: 0, display: "flex", flexDirection: "column",
      // While dragging, the pointer must not select the SQL it passes over.
      userSelect: dragging ? "none" : undefined,
    }}>
      <div style={{ height: `${ratio * 100}%`, minHeight: 0 }}>{top}</div>

      <div
        role="separator"
        aria-label="Resize the editor"
        aria-orientation="horizontal"
        tabIndex={0}
        onMouseDown={event => { event.preventDefault(); setDragging(true); }}
        onDoubleClick={() => setRatio(initial)}
        onKeyDown={nudge}
        title="Drag to resize · double-click to even it out"
        style={{
          height: 6,
          flex: "none",
          cursor: "row-resize",
          background: dragging
            ? "var(--mantine-color-blue-5)"
            : "var(--mantine-color-default-border)",
        }} />

      <div style={{ flex: 1, minHeight: 0 }}>{bottom}</div>
    </div>
  );
}
