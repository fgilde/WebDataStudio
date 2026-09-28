import { useState } from "react";
import { Alert, Button, FileInput, Group, Loader, Stack } from "@mantine/core";
import { IconDatabase, IconServer } from "@tabler/icons-react";
import { uploadConnectionFile, type Connection } from "../api";
import { ServerBrowser } from "./ServerBrowser";
import { useAccess } from "./useAccess";

/// Two ways to name a database file: the one on your machine, and the one the server can see.
///
/// The form's "File path" field has always taken a path on the server, typed as text — right for a
/// mounted share, useless for the file in your downloads folder, because the container cannot see
/// it. Uploading answers that; browsing answers the other half, where typing a path from memory was
/// the only way.
export interface FilePickerProps {
  /// An upload is a connection the moment the server has the file, so there is nothing left to fill
  /// in — the dialog is done.
  onUploaded: (created: Connection) => void;
  /// A picked path is not a connection yet: it fills the form in, and the engine comes with it
  /// because the server already decided which one reads that name.
  onPicked: (path: string, engine: string | null) => void;
}

export function FilePicker({ onUploaded, onPicked }: FilePickerProps) {
  const access = useAccess();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [browsing, setBrowsing] = useState(false);

  const upload = async (file: File | null) => {
    if (!file) return;

    setBusy(true);
    setProblem(null);

    try {
      onUploaded(await uploadConnectionFile(file));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };


  // Both halves are their own switch, so a deployment can take uploads and keep its own folders to
  // itself — or the other way round.
  if (!access.mayUpload && !access.mayBrowse) return null;

  return (
    <Stack gap="xs">
      <Group align="end" gap="xs" wrap="nowrap">
        {access.mayUpload && (
          <FileInput label="Database file" placeholder="SQLite, DuckDB, Parquet, CSV, NDJSON"
            aria-label="Database file" disabled={busy} style={{ flex: 1 }}
            leftSection={busy ? <Loader size={14} /> : <IconDatabase size={16} />}
            onChange={file => void upload(file)} />
        )}

        {access.mayBrowse && (
          <Button variant="default" leftSection={<IconServer size={16} />} onClick={() => setBrowsing(true)}>
            Browse the server
          </Button>
        )}
      </Group>

      {problem && <Alert color="red" variant="light">{problem}</Alert>}

      <ServerBrowser opened={browsing} onClose={() => setBrowsing(false)} mode="file"
        onPickFile={onPicked} />
    </Stack>
  );
}
