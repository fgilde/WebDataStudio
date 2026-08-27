import { useCallback, useEffect, useRef, useState } from "react";
import { ActionIcon, Group, Select, Switch, Text, Tooltip } from "@mantine/core";
import {
  IconPlayerPlay, IconPlayerStop, IconPlayerTrackNext, IconSparkles,
} from "@tabler/icons-react";
import { AssistModal } from "../assist/AssistModal";
import { QueryEditor } from "../editor/QueryEditor";
import { describeDiff, diffRows } from "../grid/diffRows";
import { ResultArea } from "./ResultArea";
import { runQuery, type QueryRun } from "./runQuery";
import { applyChunk, createResultState, type ResultState } from "./resultStore";
import { addHistory, health } from "../api";
import { preferences } from "../shell/preferences";
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
  // Watch mode: re-run every N seconds and say what moved. Null is off.
  const [watchSeconds, setWatchSeconds] = useState<number | null>(null);
  const [changed, setChanged] = useState<ReadonlySet<string> | undefined>(undefined);
  const [watchNote, setWatchNote] = useState<string | null>(null);
  // Off unless a server says it is configured; the button then does not exist.
  const [assistAvailable, setAssistAvailable] = useState(false);
  const [assistOpen, setAssistOpen] = useState(false);
  // The rows the last run produced, to diff the next one against. A ref rather than state: the
  // comparison happens inside a run, not during a render.
  const lastRows = useRef<unknown[][] | null>(null);
  // What the pre-run read found, and the statement it was about.
  const [inspection, setInspection] =
    useState<{ sql: string; findings: SqlFindingDto[] } | null>(null);

  useEffect(() => { onSqlChange?.(tabId, sql); }, [tabId, sql, onSqlChange]);

  useEffect(() => {
    health().then(state => setAssistAvailable(state.assist === true)).catch(() => {});
  }, []);

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

      // What moved since the previous run. Only the first result: a script that returns several is
      // not something to watch.
      const first = state.statements[0];
      if (first && !first.error) {
        const previous = lastRows.current;
        if (previous) {
          const diff = diffRows(previous, first.rows);
          setChanged(diff.cells);
          setWatchNote(describeDiff(diff));
        }
        lastRows.current = first.rows;
      }

      const last = state.statements[state.statements.length - 1];
      const prefs = preferences();

      // The result is kept with the entry only when that is asked for: a snapshot is a copy of the
      // data, and the workspace database is not where everybody wants one.
      const snapshot = prefs.historySnapshots && last && !last.error
        ? JSON.stringify({
          columns: last.columns.map(column => column.name),
          rows: last.rows.slice(0, prefs.snapshotRows),
          truncated: last.rows.length > prefs.snapshotRows,
        })
        : undefined;

      // History is best-effort: a failed write must never swallow the result the user is reading.
      addHistory({
        connectionId, sql: text,
        elapsedMs: Math.round(performance.now() - started),
        rowCount: last?.rows.length ?? null,
        error: last?.error?.text ?? null,
        snapshot,
      }).catch(() => {});
    }
  }, [connectionId, transactional]);

  // One run at a time: the next is scheduled when the previous finished, so a slow query cannot
  // pile up behind its own interval. An error stops the watch and says so.
  useEffect(() => {
    if (watchSeconds === null || !sql.trim()) return;

    let cancelled = false;
    let timer: number | undefined;

    const tick = async () => {
      await run(sql);
      if (cancelled) return;
      timer = window.setTimeout(tick, watchSeconds * 1000);
    };

    timer = window.setTimeout(tick, watchSeconds * 1000);
    return () => { cancelled = true; if (timer !== undefined) window.clearTimeout(timer); };
    // `sql` on purpose: watching a query the user has since edited would watch the wrong thing.
  }, [watchSeconds, sql, run]);

  const firstWatchError = result.statements.find(s => s.error)?.error ?? null;

  useEffect(() => {
    if (watchSeconds !== null && firstWatchError) {
      setWatchSeconds(null);
      setWatchNote(`watch stopped: ${firstWatchError.text}`);
    }
  }, [watchSeconds, firstWatchError]);

  // A statement with bind variables asks for them once, then runs with the values as parameters.
  const start = useCallback((text: string) => {
    const names = findParameters(text, engine);
    if (names.length === 0) return run(text);

    setPending({ sql: text, names });
    return Promise.resolve();
  }, [run, engine]);

  // Before that: a read of the SQL. An UPDATE with no WHERE, an accidental cross product, = NULL.
  // It warns and never refuses — the dialog's other button runs it anyway.
  const execute = useCallback(async (text: string) => {
    if (!text.trim() || !preferences().inspectBeforeRun) return start(text);

    const findings = (await inspectSql(connectionId, text).catch(() => []))
      .filter(finding => finding.severity === "warning");

    if (findings.length === 0) return start(text);

    setInspection({ sql: text, findings });
  }, [start, connectionId]);

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
        {assistAvailable && (
          <Tooltip label="Explain this statement, or draft one from a question">
            <ActionIcon variant="subtle" aria-label="Ask about this query"
              onClick={() => setAssistOpen(true)}>
              <IconSparkles size={16} />
            </ActionIcon>
          </Tooltip>
        )}
        <Tooltip label="Re-run this query and highlight what changed">
          <Select size="xs" w={110} ml={6} placeholder="watch off" clearable
            aria-label="Watch interval"
            data={[
              { value: "2", label: "every 2 s" },
              { value: "5", label: "every 5 s" },
              { value: "10", label: "every 10 s" },
              { value: "30", label: "every 30 s" },
            ]}
            value={watchSeconds === null ? null : String(watchSeconds)}
            onChange={value => {
              setWatchSeconds(value === null ? null : Number(value));
              setWatchNote(null);
              setChanged(undefined);
              lastRows.current = null;
            }} />
        </Tooltip>
        {watchNote && (
          <Text size="xs" c={watchNote.startsWith("watch stopped") ? "red" : "dimmed"}>{watchNote}</Text>
        )}
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
        <ResultArea result={result} changed={changed} connectionId={connectionId} sql={sql}
          onExport={onExport ? () => onExport(sql) : undefined} />
      </div>

      <AssistModal connectionId={connectionId} sql={sql} opened={assistOpen}
        onClose={() => setAssistOpen(false)} onUseStatement={setSql} />

      {inspection && (
        <InspectionDialog findings={inspection.findings}
          onCancel={() => setInspection(null)}
          onRun={() => {
            const text = inspection.sql;
            setInspection(null);
            void start(text);
          }} />
      )}

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
