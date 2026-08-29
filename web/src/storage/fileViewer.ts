import { health } from "../api";

/// The rich file viewer: MudEx's `<mudex-file-display>`, which renders the things a browser will
/// not — a spreadsheet, a Word document, a markdown file, an archive — on top of the images, PDFs
/// and video the built-in preview already shows.
///
/// It carries a WebAssembly runtime, so it is fetched the first time somebody asks to look at a
/// file and never as part of the studio. A deployment with no way out to the internet points
/// `WDS_FILE_VIEWER_URL` at its own copy, or sets it to nothing to switch the viewer off.

let loading: Promise<boolean> | null = null;
let scriptUrl: string | null | undefined;

/// Where the viewer comes from, as the server says. Asked once.
export async function viewerScriptUrl(): Promise<string | null> {
  if (scriptUrl !== undefined) return scriptUrl;

  try {
    const state = await health();
    scriptUrl = state.fileViewer?.script ?? null;
  } catch {
    // A studio that cannot answer is a studio without a viewer, not a broken page.
    scriptUrl = null;
  }

  return scriptUrl;
}

/// Loads it, once per session. Resolves false when there is nothing to load or the load failed —
/// the caller then says so rather than showing an empty box forever.
export function loadFileViewer(): Promise<boolean> {
  loading ??= (async () => {
    const url = await viewerScriptUrl();
    if (!url) return false;

    // Somebody else may have put it there already — a second studio panel, or a reload of this one.
    if (customElements.get("mudex-file-display")) return true;

    try {
      await new Promise<void>((resolve, reject) => {
        const existing = document.querySelector<HTMLScriptElement>(`script[data-mudex="${url}"]`);
        if (existing) {
          existing.addEventListener("load", () => resolve());
          existing.addEventListener("error", () => reject(new Error("could not be loaded")));
          return;
        }

        const script = document.createElement("script");
        script.src = url;
        script.async = true;
        script.dataset.mudex = url;
        script.addEventListener("load", () => resolve());
        script.addEventListener("error", () => reject(new Error("could not be loaded")));
        document.head.appendChild(script);
      });

      // The loader defines the element once its runtime is up; waiting for that is what makes the
      // difference between an empty box and a viewer.
      await customElements.whenDefined("mudex-file-display");
      return true;
    } catch {
      // Failed once, and asking again on every click would only make the studio feel slow.
      return false;
    }
  })();

  return loading;
}

/// Only for tests: forget what was loaded and what the server said.
export function resetFileViewer(): void {
  loading = null;
  scriptUrl = undefined;
}
