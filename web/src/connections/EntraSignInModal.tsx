import { useEffect, useRef, useState } from "react";
import {
  Alert, Anchor, Badge, Button, Code, CopyButton, Group, Loader, Modal, Stack, Text, TextInput,
} from "@mantine/core";
import { entraSignIn, entraSignOut, entraStatus, type EntraStatusDto } from "../api";

/// Signing in to Azure SQL, Synapse or Fabric as a person.
///
/// Nobody is standing at a browser inside a container, so the studio does not try to open one: it
/// shows the code, the person enters it on a device that has one, and the token stays on the server.
export function EntraSignInModal({ connectionId, name, opened, onClose }: {
  connectionId: string;
  name: string;
  opened: boolean;
  onClose: () => void;
}) {
  const [status, setStatus] = useState<EntraStatusDto | null>(null);
  const [tenant, setTenant] = useState("");
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  const stop = () => {
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = null;
  };

  useEffect(() => {
    if (!opened) { stop(); return; }

    entraStatus(connectionId).then(setStatus).catch(e => setError(e.message));
    return stop;
  }, [opened, connectionId]);

  const start = () => {
    setError(null);
    entraSignIn(connectionId, tenant || undefined)
      .then(first => {
        setStatus(first);
        stop();

        // The code arrives a moment after the flow starts, and the token some minutes later: one
        // poll covers both, and it stops as soon as there is nothing left to wait for.
        timer.current = window.setInterval(() => {
          entraStatus(connectionId)
            .then(next => {
              setStatus(next);
              if (next.state !== "pending" && next.state !== "starting") stop();
            })
            .catch(() => stop());
        }, 2000);
      })
      .catch(e => setError(e.message));
  };

  const signedIn = status?.state === "signed-in";

  return (
    <Modal opened={opened} onClose={() => { stop(); onClose(); }} title={`Sign in to ${name}`}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          This connection is opened as a person rather than as the studio's own identity. Where a
          managed identity is available, that is the better answer and needs none of this.
        </Text>

        {error && <Alert color="red" variant="light">{error}</Alert>}
        {status?.error && <Alert color="red" variant="light">{status.error}</Alert>}

        {status?.state === "pending" && status.userCode &&
          <Stack gap={4}>
            <Text size="sm">Enter this code, on any device with a browser:</Text>
            <Group gap="xs">
              <Code fz="lg">{status.userCode}</Code>
              <CopyButton value={status.userCode}>
                {({ copied, copy }) => (
                  <Button size="compact-xs" variant="default" onClick={copy}>
                    {copied ? "Copied" : "Copy"}
                  </Button>
                )}
              </CopyButton>
              <Loader size="xs" />
            </Group>
            {status.verificationUrl &&
              <Anchor size="sm" href={status.verificationUrl} target="_blank" rel="noreferrer">
                {status.verificationUrl}
              </Anchor>}
          </Stack>}

        {signedIn &&
          <Group gap="xs">
            <Badge color="green">signed in</Badge>
            {status?.expiresOn &&
              <Text size="xs" c="dimmed">
                until {new Date(status.expiresOn).toISOString().replace("T", " ").slice(0, 16)}
              </Text>}
          </Group>}

        {status?.state === "expired" &&
          <Text size="sm" c="dimmed">The last sign-in has expired. Signing in again is one click.</Text>}

        {!signedIn &&
          <TextInput size="xs" label="Tenant" placeholder="contoso.onmicrosoft.com or a tenant id"
            description="Only needed when your account is in more than one tenant"
            value={tenant} onChange={event => setTenant(event.currentTarget.value)} />}

        <Group justify="space-between">
          {signedIn
            ? <Button variant="default" color="red"
                      onClick={() => entraSignOut(connectionId)
                        .then(() => setStatus({ ...status!, state: "none" }))}>
                Sign out
              </Button>
            : <Button onClick={start} loading={status?.state === "starting"}>Sign in</Button>}
          <Button variant="subtle" onClick={() => { stop(); onClose(); }}>Close</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
