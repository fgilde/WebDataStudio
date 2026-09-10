import { useEffect, useRef } from "react";
import { GridStack, type GridStackNode } from "gridstack";
import "gridstack/dist/gridstack.min.css";
import "./Canvas.css";
import { COLUMNS, type Dashboard, type Widget } from "./model";

/// The canvas: twenty-four columns, drag, resize, reorder.
///
/// GridStack owns the geometry and React owns the contents, which is the only division that works
/// with a library that moves DOM nodes: each widget gets a stable host element keyed by its id, and
/// what changes inside it is React's business. Positions travel back on every change, so the
/// dashboard in state is always what the canvas looks like.
export function Canvas({ dashboard, editing, rowHeight, onLayout, children }: {
  dashboard: Dashboard;
  editing: boolean;
  rowHeight?: number;
  /// Every widget's position after a drag, a resize or a reorder.
  onLayout: (positions: Record<string, { x: number; y: number; w: number; h: number }>) => void;
  /// One node per widget, keyed by widget id — rendered into that widget's host element.
  children: (widget: Widget) => React.ReactNode;
}) {
  const host = useRef<HTMLDivElement>(null);
  const grid = useRef<GridStack | null>(null);
  const layout = useRef(onLayout);
  layout.current = onLayout;

  useEffect(() => {
    if (!host.current) return;

    grid.current = GridStack.init({
      column: COLUMNS,
      cellHeight: rowHeight ?? 40,
      margin: 4,
      float: true,
      disableDrag: !editing,
      disableResize: !editing,
      // The title bar is the handle: dragging a chart should pan the chart, not the widget.
      handle: ".wds-widget-handle",
      animate: false,
    }, host.current);

    const report = () => {
      const positions: Record<string, { x: number; y: number; w: number; h: number }> = {};

      for (const node of grid.current?.engine.nodes ?? []) {
        const id = (node as GridStackNode).id;
        if (typeof id !== "string") continue;
        positions[id] = { x: node.x ?? 0, y: node.y ?? 0, w: node.w ?? 1, h: node.h ?? 1 };
      }

      layout.current(positions);
    };

    const instance = grid.current!;
    instance.on("change", report);
    instance.on("resizestop", report);

    return () => {
      grid.current?.off("change");
      grid.current?.off("resizestop");
      grid.current?.destroy(false);
      grid.current = null;
    };
    // Re-initialised when the mode changes: GridStack's own enable/disable leaves handles behind.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editing, rowHeight]);

  // React owns the children, so GridStack has to be told about them.
  //
  // `init` adopts the elements that exist at that moment and nothing after: a widget added from the
  // palette, and every widget of the next dashboard the picker opens, are elements it has never
  // seen — unmanaged, so it positions none of them and they all pile up in the corner. So every
  // change reconciles both ways: adopt what is new, forget what has left the DOM, and move what
  // stayed.
  useEffect(() => {
    const instance = grid.current;
    if (!instance) return;

    instance.batchUpdate(true);

    const known = new Map(instance.engine.nodes
      .filter(node => node.el)
      .map(node => [node.el!, node]));

    for (const node of [...known.keys()])
      if (!node.isConnected) {
        // The DOM node is React's to remove; this only takes it out of the engine.
        instance.removeWidget(node, false);
        known.delete(node);
      }

    for (const widget of dashboard.widgets) {
      const element = host.current?.querySelector<HTMLElement>(`[gs-id="${widget.id}"]`);
      if (!element) continue;

      const position = {
        x: widget.position.x, y: widget.position.y,
        w: widget.position.w, h: widget.position.h,
      };

      if (known.has(element)) instance.update(element, position);
      else instance.makeWidget(element, position);
    }

    instance.batchUpdate(false);
  }, [dashboard.widgets]);

  return (
    <div className="grid-stack" ref={host}>
      {dashboard.widgets.map(widget => (
        <div key={widget.id} className="grid-stack-item" gs-id={widget.id}
          gs-x={widget.position.x} gs-y={widget.position.y}
          gs-w={widget.position.w} gs-h={widget.position.h}>
          <div className="grid-stack-item-content">{children(widget)}</div>
        </div>
      ))}
    </div>
  );
}
