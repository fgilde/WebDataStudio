import { useEffect, useState } from "react";
import { Alert, Button, Code, Group, Loader, Modal, Stack, Text } from "@mantine/core";
import { IconCopy, IconDownload } from "@tabler/icons-react";
import { readDataDictionary } from "../api";

/// "What is in this database?" — as one file you can send to somebody who does not have the studio
/// open. Everything in it is already known here; what was missing was saying it in order.
export function DataDictionaryModal({ target, onClose }: {
  target: { connectionId: string; label: string } | null;
  onClose: () => void;
}) {
  const [markdown, setMarkdown] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!target) return;

    let alive = true;
    setMarkdown(null);
    setError(null);

    readDataDictionary(target.connectionId)
      .then(text => { if (alive) setMarkdown(text); })
      .catch(e => { if (alive) setError(e instanceof Error ? e.message : String(e)); });

    return () => { alive = false; };
  }, [target]);

  const download = () => {
    if (!markdown || !target) return;

    const url = URL.createObjectURL(new Blob([markdown], { type: "text/markdown" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = `${target.label}-dictionary.md`;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Modal opened={target !== null} onClose={onClose} size="xl"
      title={`Data dictionary — ${target?.label ?? ""}`}>
      <Stack gap="sm">
        {!markdown && !error && (
          <Group gap="xs">
            <Loader size="xs" />
            <Text size="xs" c="dimmed">reading every table…</Text>
          </Group>
        )}

        {error && <Alert color="red" p="xs"><Text size="xs">{error}</Text></Alert>}

        {markdown && (
          <>
            <Code block style={{ fontSize: 11, maxHeight: "60vh", overflow: "auto" }}>
              {markdown}
            </Code>

            <Group justify="flex-end" gap="xs">
              <Button size="xs" variant="default" leftSection={<IconCopy size={13} />}
                onClick={() => navigator.clipboard.writeText(markdown)}>
                Copy
              </Button>
              <Button size="xs" leftSection={<IconDownload size={13} />} onClick={download}>
                Download
              </Button>
            </Group>
          </>
        )}
      </Stack>
    </Modal>
  );
}
