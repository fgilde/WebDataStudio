import { useState } from "react";
import type { FormEvent } from "react";
import {
  Anchor, Button, Card, Center, Group, PasswordInput, Stack, Text, TextInput, Title,
} from "@mantine/core";
import { IconBrandGithub } from "@tabler/icons-react";
import { login } from "../api";
import { DOCS_URL, GILDE_URL, GITHUB_URL } from "../components/BrandLinks";

export function LoginPage({ title, onSuccess }: { title?: string | null; onSuccess: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try { await login(username, password); onSuccess(); }
    catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  return (
    <Center h="100vh" p="md">
      <Stack align="center" gap="lg" w="100%" maw={420}>
        {/* The icon at a size worth looking at: this screen has nothing else to show. */}
        <img src="/brand/icon.svg" alt="WebDataStudio" width={112} height={112}
          style={{ display: "block", filter: "drop-shadow(0 8px 24px rgba(0,0,0,.35))" }} />

        <Stack align="center" gap={2}>
          <Title order={2} fw={700}>WebDataStudio</Title>
          {title ? (
            <Text size="sm" c="dimmed" tt="uppercase" style={{ letterSpacing: "0.12em" }}>
              {title}
            </Text>
          ) : null}
        </Stack>

        <Card withBorder padding="lg" w="100%" shadow="sm">
          <form onSubmit={submit}>
            <Stack>
              <TextInput label="User" value={username} autoFocus
                onChange={e => setUsername(e.currentTarget.value)} />
              <PasswordInput label="Password" value={password}
                onChange={e => setPassword(e.currentTarget.value)} />
              {error && <Text c="red" size="sm">{error}</Text>}
              <Button type="submit" loading={busy}>Sign in</Button>
            </Stack>
          </form>
        </Card>

        <Group gap="lg" justify="center" wrap="wrap">
          <Anchor href={GILDE_URL} target="_blank" rel="noreferrer" underline="never" c="dimmed">
            <Group gap={6} wrap="nowrap">
              <img src="/brand/gilde.png" alt="" width={18} height={18} style={{ display: "block" }} />
              <Text size="xs">gilde.org</Text>
            </Group>
          </Anchor>

          <Anchor href={GITHUB_URL} target="_blank" rel="noreferrer" underline="never" c="dimmed">
            <Group gap={6} wrap="nowrap">
              <IconBrandGithub size={18} />
              <Text size="xs">GitHub</Text>
            </Group>
          </Anchor>

          <Anchor href={DOCS_URL} target="_blank" rel="noreferrer" underline="never" c="dimmed">
            <Group gap={6} wrap="nowrap">
              <img src="/brand/icon.svg" alt="" width={18} height={18} style={{ display: "block" }} />
              <Text size="xs">Documentation</Text>
            </Group>
          </Anchor>
        </Group>
      </Stack>
    </Center>
  );
}
