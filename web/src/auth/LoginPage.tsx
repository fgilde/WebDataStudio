import { useState } from "react";
import type { FormEvent } from "react";
import { Button, Card, Center, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { login } from "../api";

export function LoginPage({ onSuccess }: { onSuccess: () => void }) {
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
    <Center h="100vh">
      <Card withBorder padding="lg" w={360}>
        <form onSubmit={submit}>
          <Stack>
            <Title order={3}>WebDataStudio</Title>
            <TextInput label="User" value={username} onChange={e => setUsername(e.currentTarget.value)} autoFocus />
            <PasswordInput label="Password" value={password} onChange={e => setPassword(e.currentTarget.value)} />
            {error && <Text c="red" size="sm">{error}</Text>}
            <Button type="submit" loading={busy}>Sign in</Button>
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
