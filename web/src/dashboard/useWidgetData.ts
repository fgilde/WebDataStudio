import { useCallback, useEffect, useRef, useState } from "react";
import { runFederation } from "../federate/runFederation";
import { runQuery, type DashboardRunContext, type QueryChunk } from "../query/runQuery";
import type { Dashboard, Widget } from "./model";

export interface WidgetData {
  columns: { name: string; dataType?: string | null; masked?: boolean }[];
  rows: unknown[][];
  running: boolean;
  error: string | null;
  truncated: boolean;
  elapsedMs: number | null;
}

const empty: WidgetData = {
  columns: [], rows: [], running: false, error: null, truncated: false, elapsedMs: null,
};

/// What a widget's statement reads from the dashboard around it.
export const contextOf = (dashboard: Dashboard, chosen: Record<string, string[]>,
  widthPx?: number): DashboardRunContext => ({
  from: dashboard.timeRange?.from ?? "now-24h",
  to: dashboard.timeRange?.to ?? "now",
  variables: chosen,
  widthPx,
});

/// One widget's rows.
///
/// A widget runs through the same path a query tab runs through — or through the studio's own
/// federation when it spans connections — so nothing here re-implements the row cap, masking or the
/// audit line. What it adds is the three things a dashboard needs: the dashboard's context travels
/// with the statement, a run in flight is abandoned when another starts, and an error is a value
/// rather than an exception, because one broken widget must not blank a page.
export function useWidgetData(widget: Widget, dashboard: Dashboard,
  chosen: Record<string, string[]>, nonce: number, widthPx?: number): WidgetData {
  const [data, setData] = useState<WidgetData>(empty);
  // A run this widget no longer cares about: its chunks are dropped rather than cancelled, because
  // the server is already streaming and a cancel is a second round trip for nothing.
  const generation = useRef(0);

  const key = JSON.stringify([widget.source, dashboard.timeRange, chosen, nonce]);

  const run = useCallback(() => {
    const mine = ++generation.current;
    const source = widget.source;
    const context = contextOf(dashboard, chosen, widthPx);

    const federated = source.kind === "Federated";
    const sources = source.sources ?? [];

    if (federated ? sources.length === 0 : !(source.connectionId && source.sql?.trim())) {
      setData({ ...empty, error: null });
      return;
    }

    const columns: WidgetData["columns"] = [];
    const rows: unknown[][] = [];
    let error: string | null = null;
    let truncated = false;
    let elapsedMs: number | null = null;

    setData(current => ({ ...current, running: true, error: null }));

    const onChunk = (chunk: QueryChunk) => {
      switch (chunk.type) {
        case "columns":
          // A widget draws one statement; a script that returns several is the first one.
          if (columns.length === 0) columns.push(...chunk.columns);
          break;
        case "rows":
          if (chunk.statement === 0) rows.push(...chunk.rows);
          break;
        case "documents":
          // A document engine answers documents; a widget reads them as one column of JSON, which
          // is what the grid does with them too.
          if (columns.length === 0) columns.push({ name: "document" });
          rows.push(...chunk.documents.map(one => [JSON.stringify(one)]));
          break;
        case "end":
          truncated = truncated || chunk.truncated;
          elapsedMs = chunk.elapsedMs;
          break;
        case "error":
          error = chunk.text;
          break;
        default:
          break;
      }
    };

    const finished = () => {
      if (mine !== generation.current) return;
      setData({ columns, rows, running: false, error, truncated, elapsedMs });
    };

    if (federated) {
      runFederation({
        sources: sources.map(one => ({ ...one })),
        sql: source.sql ?? "",
        maxRowsPerSource: source.maxRowsPerSource ?? undefined,
        dashboard: context,
      }, onChunk).then(finished, (e: unknown) => {
        if (mine !== generation.current) return;
        setData({ ...empty, error: e instanceof Error ? e.message : String(e) });
      });

      return;
    }

    runQuery({
      connectionId: source.connectionId!,
      sql: source.sql!,
      maxRows: widget.options.limit ?? 5000,
      dashboard: context,
    }, onChunk).done.then(finished);
    // The key is the whole reason to re-run; the widget's title or position is not.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  useEffect(() => { run(); }, [run]);

  return data;
}
