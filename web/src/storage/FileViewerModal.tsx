import { useEffect, useState } from "react";
import { Alert, Group, Loader, Modal, Text } from "@mantine/core";
import { loadFileViewer } from "./fileViewer";

/// What to look at: a URL the browser can fetch, and what it is.
export interface ViewableFile {
  url: string;
  name: string;
  contentType?: string | null;
}

/// A custom element is not in React's element list, and React 19 keeps that list on the module
/// rather than in the global namespace.
declare module "react" {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace JSX {
    interface IntrinsicElements {
      "mudex-file-display": React.DetailedHTMLProps<React.HTMLAttributes<HTMLElement>, HTMLElement> & {
        url?: string;
        "content-type"?: string;
        "file-name"?: string;
        "show-file-name"?: string;
        dense?: string;
      };
    }
  }
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
            The file viewer could not be loaded. It is fetched from the internet the first time it
            is used; a studio that cannot reach it — or was told to do without one — still previews
            images, PDFs, video, audio and text where the file lies, and downloads anything else.
          </Text>
        </Alert>
      )}

      {state === "ready" && file && (
        // Keyed by url: opening a second file must rebuild the element rather than hand the same
        // one a new attribute, which the component does not watch for.
        <mudex-file-display
          key={file.url}
          url={file.url}
          content-type={file.contentType ?? undefined}
          file-name={file.name}
          show-file-name="false"
          dense="true"
          style={{ display: "block", width: "100%", height: "100%" }} />
      )}
    </Modal>
  );
}
