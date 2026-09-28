import { useEffect, useRef, useState } from "react";
import * as monaco from "monaco-editor";
import { useAppTheme } from "../ThemeProvider";
import { configureMonaco } from "./monacoSetup";
import { useActiveStatement } from "./useActiveStatement";
import { useRedisLanguageFeatures } from "./useRedisLanguageFeatures";
import { tableAt, useSqlLanguageFeatures } from "./useSqlLanguageFeatures";
import type { TableRef } from "./schemaCache";
import type { Snippet } from "./snippets";
import { formatSql } from "./formatSql";
import { readFontSize, writeFontSize, zoomFor } from "./editorZoom";
import { textToRun, type DialectId } from "../sql/splitStatements";
import type { QueryError } from "../query/resultStore";

export function QueryEditor({ value, dialect, language = "sql", connectionId, error,
  onChange, onRun, onRunAll, onObjectMenu, snippets = [], commands }: {
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
  /// Ctrl+click or F12 on a table or view: what to do with it, in a menu where the pointer is.
  onObjectMenu?: (table: TableRef, at: { x: number; y: number }) => void;
  snippets?: Snippet[];
  /// The editor's own "Run", for a toolbar button: the selection or the statement under the cursor,
  /// exactly what F5 and Ctrl+Enter run.
  commands?: React.MutableRefObject<EditorCommands | null>;
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
      fontSize: readFontSize(), scrollBeyondLastLine: false, renderWhitespace: "selection",
      tabSize: 2,
    });
    setEditor(instance);

    const sub = instance.onDidChangeModelContent(() => onChange(instance.getValue()));

    // Ctrl and the wheel is what every editor does; Ctrl with plus, minus and zero is what every
    // browser does. Monaco has its own zoom, and it zooms the whole workbench rather than the text
    // of one editor — this is the one people mean.
    const apply = (size: number | null) => {
      if (size === null) return false;
      instance.updateOptions({ fontSize: writeFontSize(size) });
      return true;
    };

    const size = () => instance.getOption(monaco.editor.EditorOption.fontSize);

    // On the host element rather than through Monaco: its own wheel event does not let a handler
    // stop the page from scrolling, and a zoom that scrolls at the same time is unusable.
    const onWheel = (event: WheelEvent) => {
      if (apply(zoomFor(event, size()))) {
        event.preventDefault();
        event.stopPropagation();
      }
    };

    const element = host.current;
    element.addEventListener("wheel", onWheel, { passive: false });

    const keys = instance.onKeyDown(event => {
      if (apply(zoomFor(event.browserEvent, size()))) {
        event.preventDefault();
        event.stopPropagation();
      }
    });

    return () => {
      element.removeEventListener("wheel", onWheel);
      sub.dispose();
      keys.dispose();
      instance.dispose();
    };
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
  useRedisLanguageFeatures(connectionId, language === "plaintext");
  useSqlLanguageFeatures(language === "sql" ? connectionId : "", dialect, snippets);

  // The object menu, the same one the explorer shows for that object.
  useEffect(() => {
    if (!editor || language !== "sql" || !onObjectMenu) return;

    const open = async (position: monaco.IPosition, at: { x: number; y: number }) => {
      const model = editor.getModel();
      const table = model ? await tableAt(connectionId, model, position) : undefined;
      if (table) onObjectMenu(table, at);
    };

    const click = editor.onMouseUp(event => {
      const browser = event.event.browserEvent;
      if (!(browser.ctrlKey || browser.metaKey) || !event.target.position) return;
      void open(event.target.position, { x: browser.clientX, y: browser.clientY });
    });

    // With Ctrl held, a name the studio knows is underlined like a link. Drawn here rather than
    // through a definition provider: Monaco would follow that one itself, to the word it is on,
    // and say it found nothing.
    const link = editor.createDecorationsCollection();
    let hovered = 0;
    const move = editor.onMouseMove(event => {
      const browser = event.event.browserEvent;
      const position = event.target.position;
      const model = editor.getModel();
      const word = position && model?.getWordAtPosition(position);
      const ticket = ++hovered;
      if (!(browser.ctrlKey || browser.metaKey) || !position || !model || !word) { link.clear(); return; }

      void tableAt(connectionId, model, position).then(table => {
        if (ticket !== hovered) return;
        if (!table) { link.clear(); return; }
        link.set([{
          range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
          options: { inlineClassName: "wds-object-link" },
        }]);
      });
    });
    const release = editor.onKeyUp(event => {
      if (event.keyCode === monaco.KeyCode.Ctrl || event.keyCode === monaco.KeyCode.Meta) link.clear();
    });
    const leave = editor.onMouseLeave(() => link.clear());

    // F12 replaces Monaco's own go-to-definition, which here would only land on the same word.
    const key = editor.addAction({
      id: "wds.objectMenu", label: "Object under the cursor…", keybindings: [monaco.KeyCode.F12],
      run: () => {
        const position = editor.getPosition();
        const visible = position && editor.getScrolledVisiblePosition(position);
        const box = editor.getDomNode()?.getBoundingClientRect();
        if (position && visible && box)
          void open(position, { x: box.left + visible.left, y: box.top + visible.top + visible.height });
      },
    });

    return () => { click.dispose(); key.dispose(); move.dispose(); release.dispose(); leave.dispose(); link.clear(); };
  }, [editor, language, connectionId, onObjectMenu]);

  useEffect(() => { markErrors(editor, error); }, [editor, error]);

  // Keybindings are re-registered whenever the callbacks change so they never close over stale state.
  useEffect(() => {
    if (!editor) return;

    const run = () => {
      const model = editor.getModel();
      const selection = editor.getSelection();
      const position = editor.getPosition();
      if (!model) return;

      const text = textToRun(model.getValue(),
        selection && !selection.isEmpty() ? model.getValueInRange(selection) : null,
        position ? model.getOffsetAt(position) : model.getValueLength(), dialect);
      if (text) onRun(text);
    };
    const formatAll = () => editor.setValue(formatSql(editor.getValue(), dialect));
    if (commands) commands.current = { runCurrent: run, format: formatAll };

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
      run: formatAll,
    });

    return () => {
      runOne.dispose(); runAll.dispose(); format.dispose();
      if (commands) commands.current = null;
    };
  }, [editor, dialect, onRun, onRunAll]);

  return <div ref={host} style={{ height: "100%", width: "100%" }} />;
}

/// What the tab around the editor may ask of it: the toolbar and the shell's commands do the same
/// thing the editor's own keys do.
export interface EditorCommands { runCurrent: () => void; format: () => void }

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
