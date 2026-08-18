import { useEffect, useMemo, useState } from "react";
import { Kbd, Modal, ScrollArea, Stack, Table, Text, TextInput, UnstyledButton } from "@mantine/core";
import { filterCommands, type Command } from "./commands";

export function CommandPalette({ commands, opened, onClose }: {
  commands: Command[];
  opened: boolean;
  onClose: () => void;
}) {
  const [search, setSearch] = useState("");
  const [cursor, setCursor] = useState(0);

  const matches = useMemo(() => filterCommands(commands, search), [commands, search]);

  useEffect(() => { if (opened) { setSearch(""); setCursor(0); } }, [opened]);

  const activate = (command: Command | undefined) => {
    if (!command || command.disabled) return;
    onClose();
    command.run();
  };

  return (
    <Modal opened={opened} onClose={onClose} withCloseButton={false} size="lg" padding="xs"
      styles={{ body: { paddingTop: 8 } }}>
      <TextInput size="sm" data-autofocus placeholder="Type a command" value={search}
        onChange={e => { setSearch(e.currentTarget.value); setCursor(0); }}
        onKeyDown={e => {
          if (e.key === "ArrowDown") { e.preventDefault(); setCursor(c => Math.min(c + 1, matches.length - 1)); }
          if (e.key === "ArrowUp") { e.preventDefault(); setCursor(c => Math.max(c - 1, 0)); }
          if (e.key === "Enter") { e.preventDefault(); activate(matches[cursor]); }
        }} />

      <ScrollArea h={340} mt="xs">
        <Stack gap={0}>
          {matches.length === 0
            ? <Text size="xs" c="dimmed" p="xs">Nothing matches.</Text>
            : matches.map((command, index) => (
              <UnstyledButton key={command.id} onClick={() => activate(command)}
                onMouseEnter={() => setCursor(index)}
                style={{
                  padding: "6px 8px", borderRadius: 4,
                  background: index === cursor ? "var(--mantine-primary-color-light)" : undefined,
                  opacity: command.disabled ? 0.5 : 1,
                }}>
                <Text size="sm" component="span">{command.label}</Text>
                <Text size="xs" c="dimmed" component="span"> · {command.group}</Text>
                {command.shortcut ? <Kbd ml={8} size="xs">{command.shortcut}</Kbd> : null}
              </UnstyledButton>
            ))}
        </Stack>
      </ScrollArea>
    </Modal>
  );
}

export function ShortcutsHelp({ commands, opened, onClose }: {
  commands: Command[];
  opened: boolean;
  onClose: () => void;
}) {
  // The same registry the palette reads, so the two lists cannot drift apart.
  const groups = [...new Set(commands.map(c => c.group))];

  return (
    <Modal opened={opened} onClose={onClose} title="Keyboard shortcuts" size="lg">
      <Stack gap="md">
        {groups.map(group => (
          <div key={group}>
            <Text size="sm" fw={600} mb={4}>{group}</Text>
            <Table fz="xs" striped>
              <Table.Tbody>
                {commands.filter(c => c.group === group).map(command => (
                  <Table.Tr key={command.id}>
                    <Table.Td>{command.label}</Table.Td>
                    <Table.Td w={140} align="right">
                      {command.shortcut
                        ? <Kbd size="xs">{command.shortcut}</Kbd>
                        : <Text size="xs" c="dimmed">palette only</Text>}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </div>
        ))}

        <Text size="xs" c="dimmed">
          Ctrl+K opens the command palette; every action above is reachable from there.
        </Text>
      </Stack>
    </Modal>
  );
}
