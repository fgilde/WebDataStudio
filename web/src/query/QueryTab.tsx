import { useCallback, useEffect, useRef, useState } from "react";
import { ActionIcon, Group, Switch, Text, Tooltip } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconPlayerTrackNext } from "@tabler/icons-react";
import { QueryEditor } from "../editor/QueryEditor";
import { ResultArea } from "./ResultArea";
import { runQuery, type QueryRun } from "./runQuery";
import { applyChunk, createResultState, type ResultState } from "./resultStore";
import { addHistory } from "../api";
import { findParameters } from "../editor/parameters";
import { ParameterDialog } from "../editor/ParameterDialog";
import { useUserSnippets } from "../editor/SnippetManager";
import type { DialectId } from "../sql/splitStatements";

export interface QueryTabState { connectionId: string; dialect: DialectId; sql: string }

export function QueryTab({ tabId, connectionId, dialect, engine = "postgresql", initialSql = "",
  onSqlChange, onOpenObject, onExport }: {
  tabId: string;
  connectionId: string;
  dialect: DialectId;
  engine?: string;
  initialSql?: string;
  // Must be referentially stable: this fires on every keystroke, and an inline closure here would
  // re-run the reporting effect on every render and loop through the parent's state.
  onSqlChange?: (tabId: string, sql: string) => void;
  onOpenObject?: (ref: string) => void;
  onExport?: (sql: string) => void;
}) {
  const [sql, setSql] = useState(initialSql);
  const [result, setResult] = useState<ResultState>(createResultState);
  const [running, setRunning] = useState(false);
  const activeRun = useRef<QueryRun | null>(null);
  const [pending, setPending] = useState<{ sql: string; names: string[] } | null>(null);
  // Remembered per tab: re-running the same query with a different id is the common case.
  const [lastValues, setLastValues] = useState<Record<string, string>>({});
  const [snippets] = useUserSnippets();
  // Off means the engine's own auto-commit; on wraps the whole script in one transaction.
  const [transactional, setTransactional] = useState(false);

  useEffect(() => { onSqlChange?.(tabId, sql); }, [tabId, sql, onSqlChange]);

  const run = useCallback(async (text: string, parameters?: Record<string, string | null>) => {
    if (!text.trim()) return;

    setResult(createResultState());
    setRunning(true);
    const started = performance.now();

    let state = createResultState();
    const active = runQuery({ connectionId, sql: text, parameters, transactional }, chunk => {
      state = applyChunk(state, chunk);
      setResult(state);
    });
    activeRun.current = active;

    try {
      await active.done;
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
  }, [connectionId, transactional]);

  // A statement with bind variables asks for them once, then runs with the values as parameters.
  const execute = useCallback((text: string) => {
    const names = findParameters(text, engine);
    if (names.length === 0) return run(text);

    setPending({ sql: text, names });
    return Promise.resolve();
  }, [run, engine]);

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
        <Tooltip label="Run the whole script in one transaction and roll it back on the first error">
          <Switch size="xs" ml={6} label="single transaction" checked={transactional}
            onChange={e => setTransactional(e.currentTarget.checked)} />
        </Tooltip>
        {result.cancelled && <Text size="xs" c="orange">cancelled</Text>}
      </Group>

      <div style={{ flex: 1, minHeight: 100 }}>
        <QueryEditor value={sql} dialect={dialect} connectionId={connectionId} error={firstError}
          language={engine === "mongodb" ? "javascript" : engine === "redis" ? "plaintext" : "sql"}
          onChange={setSql} onRun={execute} onRunAll={execute} onOpenObject={onOpenObject}
          snippets={snippets} />
      </div>
      <div style={{
        flex: 1, minHeight: 100,
        borderTop: "1px solid var(--mantine-color-default-border)",
      }}>
        <ResultArea result={result} onExport={onExport ? () => onExport(sql) : undefined} />
      </div>

      <ParameterDialog names={pending?.names ?? null} initial={lastValues}
        onCancel={() => setPending(null)}
        onRun={values => {
          const text = pending?.sql ?? "";
          setLastValues(values);
          setPending(null);
          void run(text, values);
        }} />
    </div>
  );
}
