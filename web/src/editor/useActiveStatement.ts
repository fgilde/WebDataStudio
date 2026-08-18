import { useEffect } from "react";
import * as monaco from "monaco-editor";
import { statementAt, type DialectId } from "../sql/splitStatements";

// Highlights the statement the cursor sits in, so F5 never runs something the user did not expect.
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

      const start = model.getPositionAt(statement.start);
      const end = model.getPositionAt(statement.end);
      collection.set([{
        range: new monaco.Range(start.lineNumber, 1, end.lineNumber, model.getLineMaxColumn(end.lineNumber)),
        options: { isWholeLine: true, className: "wds-active-statement" },
      }]);
    };

    const cursor = editor.onDidChangeCursorPosition(update);
    const content = editor.onDidChangeModelContent(update);
    update();

    return () => { cursor.dispose(); content.dispose(); collection.clear(); };
  }, [editor, dialect]);
}
