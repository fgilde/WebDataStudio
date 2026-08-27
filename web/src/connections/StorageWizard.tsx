import { useState } from "react";
import {
  Alert, Badge, Button, Code, ColorInput, Group, Modal, PasswordInput, SegmentedControl, Select,
  Stack, Switch, Text, TextInput, Textarea,
} from "@mantine/core";
import { createConnection, testConnection, type ConnectionInput } from "../api";
import {
  authChoices, buildStorageUrl, emptyDraft, maskStorageUrl, storageProblems, suggestName,
  type StorageDraft, type StorageProvider,
} from "./storageUrl";

const PROVIDERS: { value: StorageProvider; label: string }[] = [
  { value: "s3", label: "S3" },
  { value: "azblob", label: "Azure Blob" },
  { value: "gs", label: "Google Cloud" },
  { value: "file", label: "Folder" },
];

const ABOUT: Record<StorageProvider, string> = {
  s3: "AWS, and anything else speaking S3: MinIO, Cloudflare R2, Wasabi, Ceph — those need an endpoint.",
  azblob: "Azure Blob Storage, and Azurite while developing.",
  gs: "Google Cloud Storage. Querying a file goes through the S3 protocol, which wants HMAC keys.",
  file: "A directory in the container or on a mounted volume. Nothing to authenticate.",
};

/// Adding a bucket without writing a URL.
///
/// A storage connection is a URL, which is right for a `WDS_CONN_*` in a Compose file and wrong as a
/// thing to type: nobody remembers whether the account goes in the host or the path, or which of
/// `?key=`, `?sas=` and `?connectionstring=` Azure wants. So this asks for the pieces, shows the URL
/// it builds with the secrets masked, and offers to reach the bucket before anything is saved.
export function StorageWizard({ opened, onClose, onCreated }: {
  opened: boolean;
  onClose: () => void;
  /// The connection list is re-read once one is added.
  onCreated?: () => void;
}) {
  const [draft, setDraft] = useState<StorageDraft>(emptyDraft);
  const [name, setName] = useState("");
  const [readOnly, setReadOnly] = useState(false);
  const [colour, setColour] = useState<string | null>(null);
  const [probe, setProbe] = useState<{ ok: boolean; message: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const patch = (change: Partial<StorageDraft>) => {
    setProbe(null);
    setDraft(current => ({ ...current, ...change }));
  };

  const problems = storageProblems(draft);
  const url = problems.length === 0 ? buildStorageUrl(draft) : "";
  const chosenName = name.trim() || suggestName(draft);

  const input = (): ConnectionInput => ({
    name: chosenName, engine: "storage", connectionString: url, readOnly, color: colour,
  });

  const test = () => {
    setBusy(true);
    setError(null);
    testConnection(input())
      .then(setProbe)
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  const save = () => {
    setBusy(true);
    setError(null);
    createConnection(input())
      .then(() => {
        setDraft(emptyDraft);
        setName("");
        setProbe(null);
        onCreated?.();
        onClose();
      })
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  const auth = authChoices(draft.provider);
  const azure = draft.provider === "azblob";
  const s3 = draft.provider === "s3";
  const google = draft.provider === "gs";
  const folder = draft.provider === "file";

  return (
    <Modal opened={opened} onClose={onClose} size="lg" title="Add a bucket">
      <Stack gap="sm">
        <SegmentedControl fullWidth data={PROVIDERS} value={draft.provider}
          onChange={value => {
            const provider = value as StorageProvider;
            // Each provider has its own auth choices; keeping the previous one would mean an Azure
            // SAS on an S3 bucket.
            patch({ provider, auth: authChoices(provider)[0].value });
          }} />

        <Text size="xs" c="dimmed">{ABOUT[draft.provider]}</Text>

        {folder
          ? <TextInput size="xs" label="Path" placeholder="/data/incoming" value={draft.container}
                       onChange={event => patch({ container: event.currentTarget.value })} />
          : <Group grow>
              {azure &&
                <TextInput size="xs" label="Storage account" placeholder="myaccount"
                           value={draft.account}
                           onChange={event => patch({ account: event.currentTarget.value })} />}
              <TextInput size="xs" label={azure ? "Container" : "Bucket"}
                         placeholder={azure ? "exports" : "data-lake"} value={draft.container}
                         onChange={event => patch({ container: event.currentTarget.value })} />
              <TextInput size="xs" label="Prefix" placeholder="exports/2026 (optional)"
                         value={draft.prefix}
                         onChange={event => patch({ prefix: event.currentTarget.value })} />
            </Group>}

        {s3 &&
          <Group grow>
            <TextInput size="xs" label="Region" placeholder="eu-central-1" value={draft.region}
                       onChange={event => patch({ region: event.currentTarget.value })} />
            <TextInput size="xs" label="Endpoint" placeholder="http://minio:9000 (only if not AWS)"
                       value={draft.endpoint}
                       onChange={event => patch({ endpoint: event.currentTarget.value })} />
          </Group>}

        {azure &&
          <TextInput size="xs" label="Endpoint"
                     placeholder="http://azurite:10000 (only for the emulator)" value={draft.endpoint}
                     onChange={event => patch({ endpoint: event.currentTarget.value })} />}

        {!folder &&
          <Select size="xs" label="Sign in with" data={auth} value={draft.auth}
                  description="Nothing written down is the better answer wherever it works: the managed identity, the instance role, application default credentials."
                  onChange={value => patch({ auth: (value ?? "identity") as StorageDraft["auth"] })} />}

        {draft.auth === "keys" && s3 &&
          <Group grow>
            <TextInput size="xs" label="Access key" value={draft.access}
                       onChange={event => patch({ access: event.currentTarget.value })} />
            <PasswordInput size="xs" label="Secret key" value={draft.secret}
                           onChange={event => patch({ secret: event.currentTarget.value })} />
          </Group>}

        {draft.auth === "keys" && azure &&
          <PasswordInput size="xs" label="Account key" value={draft.secret}
                         onChange={event => patch({ secret: event.currentTarget.value })} />}

        {draft.auth === "sas" &&
          <PasswordInput size="xs" label="Shared-access signature" value={draft.token}
                         placeholder="sv=2024-…&sig=…"
                         onChange={event => patch({ token: event.currentTarget.value })} />}

        {draft.auth === "connectionstring" &&
          <PasswordInput size="xs" label="Connection string" value={draft.token}
                         placeholder="DefaultEndpointsProtocol=https;AccountName=…"
                         onChange={event => patch({ token: event.currentTarget.value })} />}

        {draft.auth === "json" &&
          <Textarea size="xs" label="Service-account JSON" rows={3} value={draft.token}
                    placeholder='{"type":"service_account", …}'
                    onChange={event => patch({ token: event.currentTarget.value })} />}

        {google &&
          <Group grow>
            <TextInput size="xs" label="HMAC key" placeholder="GOOG1E… (only needed to query files)"
                       value={draft.hmac}
                       onChange={event => patch({ hmac: event.currentTarget.value })} />
            <PasswordInput size="xs" label="HMAC secret" value={draft.hmacSecret}
                           onChange={event => patch({ hmacSecret: event.currentTarget.value })} />
          </Group>}

        <Group grow>
          <TextInput size="xs" label="Name in the studio" value={name}
                     placeholder={suggestName(draft) || "LAKE"}
                     onChange={event => setName(event.currentTarget.value)} />
          <ColorInput size="xs" label="Colour" format="hex" value={colour ?? ""}
                      swatches={["#e03131", "#f08c00", "#2f9e44", "#1971c2", "#9c36b5"]}
                      description="Red marks it as production, which refuses uploads and deletes"
                      onChange={value => setColour(value || null)} />
        </Group>

        <Switch size="xs" label="Read-only" checked={readOnly}
                description="Refuses every upload and delete on this connection"
                onChange={event => setReadOnly(event.currentTarget.checked)} />

        {problems.length > 0
          ? <Alert color="gray" variant="light" title="Still needed">
              <Stack gap={0}>
                {problems.map(problem => <Text key={problem} size="xs">{problem}</Text>)}
              </Stack>
            </Alert>
          : <Stack gap={2}>
              <Text size="xs" fw={600}>This is the connection</Text>
              {/* Masked: a wizard that prints an account key in a preview is a wizard that puts it
                  in a screenshot. */}
              <Code block fz="xs">{maskStorageUrl(url)}</Code>
            </Stack>}

        {error && <Alert color="red" variant="light">{error}</Alert>}

        {probe &&
          <Alert color={probe.ok ? "green" : "orange"} variant="light">
            <Group gap="xs">
              <Badge size="xs" color={probe.ok ? "green" : "orange"}>
                {probe.ok ? "reached" : "not reached"}
              </Badge>
              <Text size="xs">{probe.message}</Text>
            </Group>
          </Alert>}

        <Group justify="space-between">
          <Button variant="default" onClick={test} loading={busy}
                  disabled={problems.length > 0 || !chosenName}>
            Test
          </Button>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            <Button onClick={save} loading={busy} disabled={problems.length > 0 || !chosenName}>
              Add
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
