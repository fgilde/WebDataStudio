import { useEffect, useRef } from "react";
import * as monaco from "monaco-editor";
import { useAppTheme } from "../ThemeProvider";
import { configureMonaco } from "../editor/monacoSetup";

/// Two texts side by side in Monaco's own diff editor: the same colours and gutter the query
/// editor uses, rather than a second, home-made diff renderer.
export function DiffView({ original, modified, language = "sql", height = 260 }: {
  original: string;
  modified: string;
  language?: string;
  height?: number;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  const { current } = useAppTheme();

  useEffect(() => {
    if (!host.current) return;
    configureMonaco();

    const instance = monaco.editor.createDiffEditor(host.current, {
      theme: current.monaco,
      automaticLayout: true,
      readOnly: true,
      renderSideBySide: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 12,
    });

    editor.current = instance;

    return () => {
      // The models belong to us, not to the editor: dispose both or they leak per mount.
      const models = instance.getModel();
      instance.dispose();
      models?.original.dispose();
      models?.modified.dispose();
      editor.current = null;
    };
  }, [current.monaco]);

  useEffect(() => {
    if (!editor.current) return;

    const previous = editor.current.getModel();
    editor.current.setModel({
      original: monaco.editor.createModel(original, language),
      modified: monaco.editor.createModel(modified, language),
    });

    previous?.original.dispose();
    previous?.modified.dispose();
  }, [original, modified, language]);

  return <div ref={host} style={{ height, width: "100%" }} />;
}
