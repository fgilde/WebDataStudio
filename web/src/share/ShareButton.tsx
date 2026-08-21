import { useEffect, useState } from "react";
import { Alert, Button, Code, CopyButton, Group, Modal, Stack, Text, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy, IconShare } from "@tabler/icons-react";
import { shareResult, shareSettings, type ShareCreatedDto } from "../api";

/// Keeps this result's rows and hands back a link — "here is what I am seeing", without a
/// screenshot. Absent unless the studio allows sharing: a link that hands rows to whoever has it is
/// a decision, not a default.
export function ShareButton({ connectionId, sql }: { connectionId: string; sql: string }) {
  const [enabled, setEnabled] = useState(false);
  const [isPublic, setPublic] = useState(false);
  const [created, setCreated] = useState<ShareCreatedDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    shareSettings()
      .then(settings => { setEnabled(settings.enabled); setPublic(settings.isPublic); })
      .catch(() => setEnabled(false));
  }, []);

  if (!enabled) return null;

  const share = async () => {
    setBusy(true);
    setError(null);
    try {
      setCreated(await shareResult(connectionId, sql));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const url = created ? new URL(created.url, window.location.origin).toString() : "";

  return (
    <>
      <Tooltip label="Keep these rows and get a link to them">
        <Button size="compact-xs" variant="default" leftSection={<IconShare size={13} />}
          loading={busy} onClick={share}>
          Share
        </Button>
      </Tooltip>

      <Modal opened={created !== null || error !== null} onClose={() => { setCreated(null); setError(null); }}
        title="A link to these rows">
        <Stack gap="sm">
          {error && <Alert color="red" p="xs"><Text size="sm">{error}</Text></Alert>}

          {created && (
            <>
              <Group gap="xs" wrap="nowrap">
                <Code style={{ flex: 1, wordBreak: "break-all" }}>{url}</Code>
                <CopyButton value={url}>
                  {({ copied, copy }) => (
                    <Button size="compact-xs" variant="light" onClick={copy}
                      leftSection={copied ? <IconCheck size={13} /> : <IconCopy size={13} />}>
                      {copied ? "copied" : "copy"}
                    </Button>
                  )}
                </CopyButton>
              </Group>

              <Text size="xs" c="dimmed">
                {created.rows} rows{created.truncated ? " (truncated)" : ""}, as they are now —
                the link shows a snapshot and cannot run anything. It expires{" "}
                {new Date(created.expiresAt).toLocaleString()}.
              </Text>

              <Alert p="xs" color={isPublic ? "orange" : "gray"}>
                <Text size="xs">
                  {isPublic
                    ? "Anybody with this link can open it, without signing in. Masked columns stay masked."
                    : "Opening this link needs an account on this studio."}
                </Text>
              </Alert>
            </>
          )}
        </Stack>
      </Modal>
    </>
  );
}
