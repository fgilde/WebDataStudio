import { Component, useEffect, useRef, useState, type ReactNode } from "react";
import { Alert, Group, Loader, Modal, Text } from "@mantine/core";
import { loadFileViewer } from "./fileViewer";

/// What to look at: a URL the browser can fetch, and what it is.
export interface ViewableFile {
  url: string;
  name: string;
  contentType?: string | null;
}

/// Nothing in a viewer for somebody else's file is worth the whole studio going grey.
class Boundary extends Component<{ children: ReactNode; onFailed: () => void }> {
  static getDerivedStateFromError() { return {}; }

  componentDidCatch(error: unknown) {
    console.error("the file viewer failed", error);
    this.props.onFailed();
  }

  render() { return this.props.children; }
}

/// The element, held outside React.
///
/// `<mudex-file-display>` is a Blazor component behind a custom element: it starts a WebAssembly
/// runtime, adds a root of its own to the page and rewrites what is inside the tag. React expects
/// to own the DOM it rendered, and the two of them fighting over one node is what turned the page
/// grey — a `style` React went to set on a node its opponent had already replaced.
///
/// So React renders an empty box and this puts the element inside it by hand. Nothing React does
/// touches the element, and nothing the element does surprises React.
function MudexFile({ file }: { file: ViewableFile }) {
  const host = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const box = host.current;
    if (!box) return;

    const element = document.createElement("mudex-file-display");

    element.setAttribute("url", file.url);
    element.setAttribute("file-name", file.name);
    if (file.contentType) element.setAttribute("content-type", file.contentType);
    // The modal's title already carries the name.
    element.setAttribute("show-file-name", "false");
    element.setAttribute("dense", "true");
    element.style.display = "block";
    element.style.width = "100%";
    element.style.height = "100%";

    box.appendChild(element);

    return () => {
      // Emptying the box rather than removing the child: the component may have moved things
      // around inside it, and this leaves nothing behind either way.
      box.replaceChildren();
    };
  }, [file.url, file.name, file.contentType]);

  return <div ref={host} style={{ width: "100%", height: "100%" }} />;
}

/// A file, shown rather than downloaded.
///
/// The built-in preview covers what a browser renders by itself. This is the rest of them — a
/// spreadsheet, a Word document, a markdown file, an archive — through MudEx's file display, which
/// is fetched the first time this opens and not before.
export function FileViewerModal({ file, onClose }: {
  file: ViewableFile | null;
  onClose: () => void;
}) {
  const [state, setState] = useState<"loading" | "ready" | "unavailable">("loading");

  useEffect(() => {
    if (!file) return;

    let alive = true;
    setState("loading");

    loadFileViewer()
      .then(ok => { if (alive) setState(ok ? "ready" : "unavailable"); })
      .catch(() => { if (alive) setState("unavailable"); });

    return () => { alive = false; };
  }, [file]);

  return (
    <Modal opened={file !== null} onClose={onClose} size="90%" title={file?.name ?? ""}
      styles={{ body: { height: "75vh", padding: 0 } }}>
      {state === "loading" && (
        <Group gap="xs" p="md">
          <Loader size="xs" />
          <Text size="xs" c="dimmed">fetching the viewer…</Text>
        </Group>
      )}

      {state === "unavailable" && (
        <Alert color="gray" m="md" p="xs">
          <Text size="xs">
            The file viewer could not be shown. It is fetched from the internet the first time it is
            used; a studio that cannot reach it — or was told to do without one — still previews
            images, PDFs, video, audio and text where the file lies, and downloads anything else.
          </Text>
        </Alert>
      )}

      {state === "ready" && file && (
        // Keyed by url: a second file gets its own element rather than a new attribute on the old
        // one, which the component does not watch for.
        <Boundary key={file.url} onFailed={() => setState("unavailable")}>
          <MudexFile file={file} />
        </Boundary>
      )}
    </Modal>
  );
}
