import { useCallback, useEffect, useRef, useState } from "react";
import { ActionIcon, Group, Text, Tooltip } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconPlayerTrackNext } from "@tabler/icons-react";
import { QueryEditor } from "../editor/QueryEditor";
import { ResultArea } from "./ResultArea";
import { runQuery, type QueryRun } from "./runQuery";
import { applyChunk, createResultState, type ResultState } from "./resultStore";
import { addHistory } from "../api";
import type { DialectId } from "../sql/splitStatements";

export interface QueryTabState { connectionId: string; dialect: DialectId; sql: string }

export function QueryTab({ tabId, connectionId, dialect, initialSql = "", onSqlChange, onOpenObject }: {
  tabId: string;
  connectionId: string;
  dialect: DialectId;
  initialSql?: string;
  // Must be referentially stable: this fires on every keystroke, and an inline closure here would
  // re-run the reporting effect on every render and loop through the parent's state.
  onSqlChange?: (tabId: string, sql: string) => void;
  onOpenObject?: (ref: string) => void;
}) {
  const [sql, setSql] = useState(initialSql);
  const [result, setResult] = useState<ResultState>(createResultState);
  const [running, setRunning] = useState(false);
  const activeRun = useRef<QueryRun | null>(null);

  useEffect(() => { onSqlChange?.(tabId, sql); }, [tabId, sql, onSqlChange]);

  const execute = useCallback(async (text: string) => {
    if (!text.trim()) return;

    setResult(createResultState());
    setRunning(true);
    const started = performance.now();

    let state = createResultState();
    const run = runQuery({ connectionId, sql: text }, chunk => {
      state = applyChunk(state, chunk);
      setResult(state);
    });
    activeRun.current = run;

    try {
      await run.done;
    } finally {
      setRunning(false);
      activeRun.current = null;

      const last = state.statements[state.statements.length - 1];
      // History is best-effort: a failed write must never swallow the result the user is reading.
      addHistory({
        connectionId, sql: text,
        elapsedMs: Math.round(performance.now() - started),
        rowCount: last?.rows.length ?? null,
        error: last?.error?.text ?? null,
      }).catch(() => {});
    }
  }, [connectionId]);

  const firstError = result.statements.find(s => s.error)?.error ?? null;

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={4} p={2}>
        <Tooltip label="Run selection or statement (F5)">
          <ActionIcon variant="subtle" aria-label="Run" disabled={running} onClick={() => execute(sql)}>
            <IconPlayerPlay size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Run whole script (Ctrl+Shift+Enter)">
          <ActionIcon variant="subtle" aria-label="Run script" disabled={running} onClick={() => execute(sql)}>
            <IconPlayerTrackNext size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Cancel">
          <ActionIcon variant="subtle" color="red" aria-label="Cancel" disabled={!running}
            onClick={() => activeRun.current?.cancel()}>
            <IconPlayerStop size={16} />
          </ActionIcon>
        </Tooltip>
        {result.cancelled && <Text size="xs" c="orange">cancelled</Text>}
      </Group>

      <div style={{ flex: 1, minHeight: 100 }}>
        <QueryEditor value={sql} dialect={dialect} connectionId={connectionId} error={firstError}
          onChange={setSql} onRun={execute} onRunAll={execute} onOpenObject={onOpenObject} />
      </div>
      <div style={{
        flex: 1, minHeight: 100,
        borderTop: "1px solid var(--mantine-color-default-border)",
      }}>
        <ResultArea result={result} />
      </div>
    </div>
  );
}
