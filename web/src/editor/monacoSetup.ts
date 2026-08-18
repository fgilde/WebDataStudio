import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

let configured = false;

// Monaco needs its worker wired up explicitly under Vite. Only the base editor worker is needed:
// SQL has no dedicated language service worker.
export function configureMonaco() {
  if (configured) return;
  configured = true;
  self.MonacoEnvironment = { getWorker: () => new editorWorker() };
  monaco.languages.register({ id: "sql" });
}
