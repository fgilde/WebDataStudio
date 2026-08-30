import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, Group, Loader, Modal, Text } from "@mantine/core";
import { viewerAvailable, viewerFrameUrl } from "./fileViewer";

/// What to look at: a URL the browser can fetch, and what it is.
export interface ViewableFile {
  url: string;
  name: string;
  contentType?: string | null;
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
  const [hasViewer, setHasViewer] = useState<boolean | undefined>(undefined);
  const [failed, setFailed] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const frame = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    if (!file) return;

    let alive = true;
    setFailed(null);
    setReady(false);

    viewerAvailable()
      .then(yes => { if (alive) setHasViewer(yes); })
      .catch(() => { if (alive) setHasViewer(false); });

    return () => { alive = false; };
  }, [file]);

  // What the frame says about itself. Only its own messages: a page in a modal should not be
  // steered by whatever else is talking on this window.
  useEffect(() => {
    if (!file) return;

    const onMessage = (event: MessageEvent) => {
      // Only this frame's own words. Another page in another tab, or anything else talking on
      // this window, is not the viewer reporting on itself.
      if (event.source !== frame.current?.contentWindow) return;

      const message = event.data as { mudex?: string; detail?: string } | null;
      if (!message?.mudex) return;

      if (message.mudex === "ready") setReady(true);
      if (message.mudex === "failed") setFailed(message.detail || "the viewer failed to start");
    };

    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  }, [file]);

  // Read from the document rather than from the theme context: this modal is opened from three
  // places, and none of them should have to carry a provider for one boolean. Mantine puts the
  // answer on <html> for exactly this kind of question.
  const dark = document.documentElement.dataset.mantineColorScheme === "dark";

  const page = useMemo(
    () => (file && hasViewer ? viewerFrameUrl(file, dark) : null),
    [file, hasViewer, dark]);

  const waiting = hasViewer === undefined || (page !== null && !ready && !failed);

  return (
    <Modal opened={file !== null} onClose={onClose} size="90%" title={file?.name ?? ""}
      styles={{ body: { height: "75vh", padding: 0 } }}>
      {waiting && (
        <Group gap="xs" p="md">
          <Loader size="xs" />
          <Text size="xs" c="dimmed">fetching the viewer…</Text>
        </Group>
      )}

      {(hasViewer === false || failed) && (
        <Alert color="gray" m="md" p="xs">
          <Text size="xs">
            The file viewer could not be shown{failed ? `: ${failed}` : ""}. It is fetched from the
            internet the first time it is used; a studio that cannot reach it — or was told to do
            without one — still previews images, PDFs, video, audio and text where the file lies,
            and downloads anything else.
          </Text>
        </Alert>
      )}

      {page && !failed && (
        // Keyed by url, so a second file gets a frame of its own rather than a new attribute on an
        // element that is not watching for one.
        <iframe
          ref={frame}
          key={page}
          title={file!.name}
          src={page}
          style={{
            border: "none",
            width: "100%",
            height: "100%",
            // Hidden rather than unmounted while it starts: unmounting would throw away the
            // runtime it is in the middle of starting.
            display: ready ? "block" : "none",
          }} />
      )}
    </Modal>
  );
}
