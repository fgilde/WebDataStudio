import { base, health } from "../api";

/// The rich file viewer: MudEx's `<mudex-file-display>`, which renders the things a browser will
/// not — a spreadsheet, a Word document, a markdown file, an archive — on top of the images, PDFs
/// and video the built-in preview already shows.
///
/// It runs on a page of its own, which the studio serves at `/api/viewer/frame`, and this module
/// only says whether there is one to open. Two reasons it cannot live in this document: the
/// component puts its stylesheets wherever it is loaded, which repaints the studio, and its
/// WebAssembly runtime refuses to start in a `srcdoc` frame — a real URL is the price of admission.

let available: boolean | undefined;

/// Whether this studio has a viewer at all. Asked once; a deployment can switch it off.
export async function viewerAvailable(): Promise<boolean> {
  if (available !== undefined) return available;

  try {
    const state = await health();
    available = Boolean(state.fileViewer?.script);
  } catch {
    // A studio that cannot answer is a studio without a viewer, not a broken page.
    available = false;
  }

  return available;
}

/// The page to put in the frame, for one file.
export function viewerFrameUrl(
  file: { url: string; name: string; contentType?: string | null },
  dark: boolean,
): string {
  const query = new URLSearchParams({ url: file.url, name: file.name });

  if (file.contentType) query.set("type", file.contentType);
  if (dark) query.set("dark", "true");

  return `${base}/viewer/frame?${query}`;
}

/// Only for tests: forget what the server said.
export function resetFileViewer(): void {
  available = undefined;
}
