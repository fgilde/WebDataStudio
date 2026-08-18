import { useEffect, useRef, useState } from "react";
import * as monaco from "monaco-editor";
import { useAppTheme } from "../ThemeProvider";
import { configureMonaco } from "./monacoSetup";
import { useActiveStatement } from "./useActiveStatement";
import { useSqlLanguageFeatures } from "./useSqlLanguageFeatures";
import type { Snippet } from "./snippets";
import { formatSql } from "./formatSql";
import { statementAt, type DialectId } from "../sql/splitStatements";
import type { QueryError } from "../query/resultStore";

export function QueryEditor({ value, dialect, language = "sql", connectionId, error,
  onChange, onRun, onRunAll, onOpenObject, snippets = [] }: {
  value: string;
  dialect: DialectId;
  /// Non-SQL engines get a different editor language: MongoDB commands read as JavaScript, Redis
  /// commands are plain text. Everything else in the tab stays the same.
  language?: "sql" | "javascript" | "plaintext";
  connectionId: string;
  error: QueryError | null;
  onChange: (sql: string) => void;
  onRun: (sql: string) => void;
  onRunAll: (sql: string) => void;
  onOpenObject?: (ref: string) => void;
  snippets?: Snippet[];
}) {
  const host = useRef<HTMLDivElement>(null);
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const { current } = useAppTheme();

  useEffect(() => {
    if (!host.current) return;
    configureMonaco();

    const instance = monaco.editor.create(host.current, {
      value, language, theme: current.monaco,
      automaticLayout: true, minimap: { enabled: false },
      fontSize: 13, scrollBeyondLastLine: false, renderWhitespace: "selection",
      tabSize: 2,
    });
    setEditor(instance);

    const sub = instance.onDidChangeModelContent(() => onChange(instance.getValue()));
    return () => { sub.dispose(); instance.dispose(); };
    // Created once: value changes flow through the model, not through recreation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Theme switches must restyle the editor without a reload.
  useEffect(() => { monaco.editor.setTheme(current.monaco); }, [current.monaco]);

  // A tab restored from the server sets its SQL after the editor already exists.
  useEffect(() => {
    if (editor && editor.getValue() !== value) editor.setValue(value);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  // Statement highlighting and schema completion only make sense for SQL.
  useActiveStatement(language === "sql" ? editor : null, dialect);
  useSqlLanguageFeatures(language === "sql" ? connectionId : "", dialect, onOpenObject, snippets);

  useEffect(() => { markErrors(editor, error); }, [editor, error]);

  // Keybindings are re-registered whenever the callbacks change so they never close over stale state.
  useEffect(() => {
    if (!editor) return;

    const run = () => {
      const model = editor.getModel();
      const selection = editor.getSelection();
      if (!model) return;

      if (selection && !selection.isEmpty()) { onRun(model.getValueInRange(selection)); return; }

      const position = editor.getPosition();
      if (!position) return;
      const statement = statementAt(model.getValue(), model.getOffsetAt(position), dialect);
      if (statement) onRun(statement.text);
    };

    const runOne = editor.addAction({
      id: "wds.run", label: "Run selection or statement",
      keybindings: [monaco.KeyCode.F5, monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
      run,
    });
    const runAll = editor.addAction({
      id: "wds.runAll", label: "Run whole script",
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.Enter],
      run: () => onRunAll(editor.getValue()),
    });
    const format = editor.addAction({
      id: "wds.format", label: "Format SQL",
      keybindings: [monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF],
      run: () => editor.setValue(formatSql(editor.getValue(), dialect)),
    });

    return () => { runOne.dispose(); runAll.dispose(); format.dispose(); };
  }, [editor, dialect, onRun, onRunAll]);

  return <div ref={host} style={{ height: "100%", width: "100%" }} />;
}

/// Turns a server error into a Monaco marker so the squiggle lands on the reported position.
export function markErrors(editor: monaco.editor.IStandaloneCodeEditor | null, error: QueryError | null) {
  const model = editor?.getModel();
  if (!model) return;

  if (!error) { monaco.editor.setModelMarkers(model, "wds", []); return; }

  const line = Math.min(error.line ?? 1, model.getLineCount());
  const column = error.column ?? 1;
  monaco.editor.setModelMarkers(model, "wds", [{
    severity: monaco.MarkerSeverity.Error,
    message: error.text,
    startLineNumber: line, startColumn: column,
    endLineNumber: line, endColumn: model.getLineMaxColumn(line),
  }]);
}
