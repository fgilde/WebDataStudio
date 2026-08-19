import { useState } from "react";
import {
  Accordion, Button, ColorInput, Group, NumberInput, PasswordInput, Select, Stack, Switch, Text,
  Textarea, TextInput,
} from "@mantine/core";
import { ENGINES, engineFromConnectionString } from "./engines";
import { testConnection, type ConnectionInput, type TunnelInput } from "../api";

const TLS_MODES = ["default", "disable", "prefer", "require", "verify-ca", "verify-full"];

/// TLS is spelled differently per provider, so the form writes the right key rather than asking
/// the user to remember which one their driver wants.
function withSslMode(connectionString: string, engine: string, mode: string): string {
  const key = engine === "mysql" ? "SslMode"
    : engine === "sqlserver" ? "Encrypt"
    : "SSL Mode";

  const value = engine === "sqlserver"
    ? (mode === "disable" ? "false" : "true")
    : mode === "verify-full" ? "VerifyFull"
    : mode === "verify-ca" ? "VerifyCA"
    : mode.charAt(0).toUpperCase() + mode.slice(1);

  const parts = connectionString.split(";").filter(p =>
    p.trim() !== "" && !p.trim().toLowerCase().startsWith(key.toLowerCase()));

  return mode === "default" ? parts.join(";") : [...parts, `${key}=${value}`].join(";");
}

export function ConnectionForm({ initial, onSubmit, onCancel }: {
  initial?: ConnectionInput;
  onSubmit: (value: ConnectionInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [value, setValue] = useState<ConnectionInput>(initial ?? {
    name: "", engine: "postgresql", connectionString: "", readOnly: false,
  });
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [sslMode, setSslMode] = useState("default");
  const [tunnel, setTunnel] = useState<TunnelInput | null>(initial?.tunnel ?? null);

  // Pasting a connection string picks the engine automatically.
  const setConnectionString = (text: string) => {
    const detected = engineFromConnectionString(text);
    setValue(v => ({ ...v, connectionString: text, engine: detected ?? v.engine }));
  };

  const patchTunnel = (patch: Partial<TunnelInput>) =>
    setTunnel(t => ({ ...(t ?? { host: "", port: 22, user: "" }), ...patch }));

  const complete = (): ConnectionInput => ({ ...value, tunnel });

  const test = async () => {
    setBusy(true);
    try { const r = await testConnection(complete()); setStatus(r.message); }
    catch (e) { setStatus(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  return (
    <Stack>
      <TextInput label="Name" value={value.name} required
        onChange={e => { const name = e.currentTarget.value; setValue(v => ({ ...v, name })); }} />
      <Select label="Engine" data={ENGINES.map(e => ({ value: e.id, label: e.label }))}
        value={value.engine} onChange={id => id && setValue(v => ({ ...v, engine: id }))} />
      <Textarea label="Connection string" autosize minRows={2} value={value.connectionString}
        onChange={e => setConnectionString(e.currentTarget.value)}
        description="A provider connection string or a URL such as postgres://user:pw@host:5432/db" />
      <Switch label="Read-only" checked={value.readOnly}
        onChange={e => { const readOnly = e.currentTarget.checked; setValue(v => ({ ...v, readOnly })); }} />

      {/* Both collapsed: the common case stays a three-field form. */}
      <Accordion variant="contained" chevronPosition="left">
        <Accordion.Item value="grouping">
          <Accordion.Control>
            <Text size="sm">Group and colour</Text>
          </Accordion.Control>
          <Accordion.Panel>
            <Stack gap="xs">
              <TextInput size="xs" label="Group" placeholder="production" value={value.group ?? ""}
                onChange={e => { const group = e.currentTarget.value || null; setValue(v => ({ ...v, group })); }} />
              <ColorInput size="xs" label="Colour" format="hex" value={value.color ?? ""}
                swatches={["#e03131", "#f08c00", "#2f9e44", "#1971c2", "#9c36b5"]}
                onChange={colour => setValue(v => ({ ...v, color: colour || null }))}
                description="Tints this connection in the explorer" />
            </Stack>
          </Accordion.Panel>
        </Accordion.Item>

        <Accordion.Item value="ssh">
          <Accordion.Control>
            <Text size="sm">SSH tunnel {tunnel ? "· on" : ""}</Text>
          </Accordion.Control>
          <Accordion.Panel>
            <Stack gap="xs">
              <Group grow>
                <TextInput size="xs" label="SSH host" value={tunnel?.host ?? ""}
                  onChange={e => patchTunnel({ host: e.currentTarget.value })} />
                <NumberInput size="xs" label="Port" min={1} max={65535} value={tunnel?.port ?? 22}
                  onChange={v => patchTunnel({ port: Number(v) || 22 })} />
              </Group>
              <TextInput size="xs" label="SSH user" value={tunnel?.user ?? ""}
                onChange={e => patchTunnel({ user: e.currentTarget.value })} />
              <PasswordInput size="xs" label="SSH password" value={tunnel?.password ?? ""}
                onChange={e => patchTunnel({ password: e.currentTarget.value || null })} />
              {/* A key wins over a password; both would just confuse the server. */}
              <Textarea size="xs" label="Private key" autosize minRows={2} maxRows={6}
                value={tunnel?.privateKey ?? ""}
                placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                onChange={e => patchTunnel({ privateKey: e.currentTarget.value || null })} />
              <PasswordInput size="xs" label="Key passphrase" value={tunnel?.passphrase ?? ""}
                onChange={e => patchTunnel({ passphrase: e.currentTarget.value || null })} />
              <Button size="compact-xs" variant="subtle" color="red" disabled={!tunnel}
                onClick={() => setTunnel(null)}>Remove the tunnel</Button>
            </Stack>
          </Accordion.Panel>
        </Accordion.Item>

        <Accordion.Item value="tls">
          <Accordion.Control><Text size="sm">TLS</Text></Accordion.Control>
          <Accordion.Panel>
            <Select size="xs" label="Mode" data={TLS_MODES} value={sslMode}
              description="Written into the connection string in this provider's spelling"
              onChange={mode => {
                const chosen = mode ?? "default";
                setSslMode(chosen);
                setValue(v => ({
                  ...v, connectionString: withSslMode(v.connectionString, v.engine, chosen),
                }));
              }} />
          </Accordion.Panel>
        </Accordion.Item>
      </Accordion>

      {status && <Text size="sm">{status}</Text>}

      <Group justify="space-between">
        <Button variant="default" onClick={test} loading={busy}>Test</Button>
        <Group>
          <Button variant="subtle" onClick={onCancel}>Cancel</Button>
          <Button onClick={() => onSubmit(complete())}
            disabled={!value.name || !value.connectionString}>Save</Button>
        </Group>
      </Group>
    </Stack>
  );
}
