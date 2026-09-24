import { useEffect } from "react";
import * as monaco from "monaco-editor";
import { framedRange, statementAt, type DialectId } from "../sql/splitStatements";

// Frames the statement the cursor sits in, the way Rider does: what Ctrl+Enter and F5 run, so
// neither ever runs something the user did not expect. A selection runs instead, and hides it.
export function useActiveStatement(
  editor: monaco.editor.IStandaloneCodeEditor | null,
  dialect: DialectId,
) {
  useEffect(() => {
    if (!editor) return;
    const collection = editor.createDecorationsCollection([]);

    const update = () => {
      const model = editor.getModel();
      const position = editor.getPosition();
      if (!model || !position) return;

      const selection = editor.getSelection();
      if (selection && !selection.isEmpty()) { collection.set([]); return; }

      const statement = statementAt(model.getValue(), model.getOffsetAt(position), dialect);
      if (!statement) { collection.set([]); return; }

      const range = framedRange(model.getValue(), statement);
      const first = model.getPositionAt(range.start).lineNumber;
      const last = model.getPositionAt(range.end).lineNumber;

      // One decoration per line: the frame's top belongs to the first, its bottom to the last.
      const lines: monaco.editor.IModelDeltaDecoration[] = [];
      for (let line = first; line <= last; line++) {
        const edge = first === last ? "single" : line === first ? "first" : line === last ? "last" : "middle";
        lines.push({
          range: new monaco.Range(line, 1, line, 1),
          options: { isWholeLine: true, className: `wds-active-statement wds-active-statement-${edge}` },
        });
      }
      collection.set(lines);
    };

    const cursor = editor.onDidChangeCursorPosition(update);
    const content = editor.onDidChangeModelContent(update);
    update();

    return () => { cursor.dispose(); content.dispose(); collection.clear(); };
  }, [editor, dialect]);
}
