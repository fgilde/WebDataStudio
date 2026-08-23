import { useState } from "react";
import {
  ActionIcon, Alert, Button, Group, Kbd, Modal, NumberInput, ScrollArea, Stack, Switch, Table, Tabs,
  Text, Tooltip,
} from "@mantine/core";
import { IconKeyboard, IconRotate } from "@tabler/icons-react";
import type { Command } from "./commands";
import { comboOf, savePreferences, usePreferences } from "./preferences";

/// Everything the studio keeps as a preference, in one place, stored in the workspace so it
/// survives a restart and follows the user to another browser.
export function PreferencesModal({ commands, opened, onClose }: {
  commands: Command[];
  opened: boolean;
  onClose: () => void;
}) {
  const prefs = usePreferences();
  const [recording, setRecording] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const store = (next: Parameters<typeof savePreferences>[0]) =>
    savePreferences(next).catch(e => setFailure(e instanceof Error ? e.message : String(e)));

  const rebind = (command: Command, event: React.KeyboardEvent) => {
    event.preventDefault();
    const combo = comboOf(event.nativeEvent);
    if (!combo) return; // still holding a modifier

    setRecording(null);
    if (combo === "Escape") return;

    void store({ shortcuts: { ...prefs.shortcuts, [command.id]: combo } });
  };

  const reset = (command: Command) => {
    const { [command.id]: _dropped, ...rest } = prefs.shortcuts;
    void store({ shortcuts: rest });
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Preferences" size="lg">
      <Tabs defaultValue="general" keepMounted={false}>
        <Tabs.List>
          <Tabs.Tab value="general">General</Tabs.Tab>
          <Tabs.Tab value="keys" leftSection={<IconKeyboard size={13} />}>Keyboard</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="general" pt="sm">
          <Stack gap="sm">
            <NumberInput size="xs" w={180} label="Rows per page in the data tab" min={20} max={2000}
              step={20} value={prefs.pageSize}
              onChange={value => store({ pageSize: Math.max(20, Number(value) || 200) })} />

            <Switch size="xs" checked={prefs.historySnapshots}
              label="Keep the result with each history entry"
              description="A snapshot is a copy of the data in the workspace database. Off by default."
              onChange={e => store({ historySnapshots: e.currentTarget.checked })} />

            <NumberInput size="xs" w={180} label="Rows a snapshot keeps" min={10} max={2000} step={10}
              disabled={!prefs.historySnapshots} value={prefs.snapshotRows}
              onChange={value => store({ snapshotRows: Math.max(10, Number(value) || 200) })} />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="keys" pt="sm">
          <Text size="xs" c="dimmed" mb={6}>
            Click a binding and press the combination. Escape keeps the current one.
          </Text>

          <ScrollArea h={340}>
            <Table fz="xs" striped>
              <Table.Tbody>
                {commands.map(command => {
                  const bound = prefs.shortcuts[command.id];

                  return (
                    <Table.Tr key={command.id}>
                      <Table.Td>{command.label}</Table.Td>
                      <Table.Td w={40}><Text size="10px" c="dimmed">{command.group}</Text></Table.Td>
                      <Table.Td w={150}>
                        <Button size="compact-xs" variant={recording === command.id ? "filled" : "default"}
                          onClick={() => setRecording(command.id)}
                          onKeyDown={event => recording === command.id && rebind(command, event)}>
                          {recording === command.id
                            ? "press a key…"
                            : bound ?? command.shortcut ?? "unbound"}
                        </Button>
                      </Table.Td>
                      <Table.Td w={32}>
                        {bound
                          ? (
                            <Tooltip label={`Back to ${command.shortcut ?? "unbound"}`}>
                              <ActionIcon size="sm" variant="subtle" aria-label="Reset binding"
                                onClick={() => reset(command)}>
                                <IconRotate size={13} />
                              </ActionIcon>
                            </Tooltip>
                          )
                          : null}
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </ScrollArea>

          <Group gap={6} mt="xs">
            <Text size="10px" c="dimmed">
              A rebound command runs from anywhere; the built-in bindings such as
            </Text>
            <Kbd size="xs">F5</Kbd>
            <Text size="10px" c="dimmed">keep working as well.</Text>
          </Group>
        </Tabs.Panel>
      </Tabs>

      {failure && <Alert color="red" mt="xs" p="xs"><Text size="xs">{failure}</Text></Alert>}
    </Modal>
  );
}
