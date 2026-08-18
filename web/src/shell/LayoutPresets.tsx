import { useEffect, useState } from "react";
import {
  ActionIcon, Button, Group, Modal, Stack, Table, Text, TextInput,
} from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { loadWorkspaceItem, saveWorkspaceItem } from "../api";

export interface LayoutPreset { name: string; connectionId: string | null; layout: unknown }

const KEY = "layout-presets";

export function useLayoutPresets() {
  const [presets, setPresets] = useState<LayoutPreset[]>([]);

  useEffect(() => {
    loadWorkspaceItem<LayoutPreset[]>(KEY)
      .then(list => setPresets(Array.isArray(list) ? list : []))
      .catch(() => setPresets([]));
  }, []);

  const save = async (list: LayoutPreset[]) => {
    setPresets(list);
    await saveWorkspaceItem(KEY, list);
  };

  return { presets, save };
}

export function LayoutPresetsModal({ opened, onClose, connectionId, capture, apply, reset }: {
  opened: boolean;
  onClose: () => void;
  connectionId: string | null;
  capture: () => unknown;
  apply: (layout: unknown) => void;
  reset: () => void;
}) {
  const { presets, save } = useLayoutPresets();
  const [name, setName] = useState("");
  const [global, setGlobal] = useState(false);

  const visible = presets.filter(p => p.connectionId === null || p.connectionId === connectionId);

  return (
    <Modal opened={opened} onClose={onClose} title="Layout presets">
      <Stack gap="sm">
        <Group gap={6} align="flex-end">
          <TextInput size="xs" flex={1} label="Name" value={name}
            onChange={e => setName(e.currentTarget.value)} />
          <Button size="compact-xs" variant="default" onClick={() => setGlobal(g => !g)}>
            {global ? "for every connection" : "for this connection"}
          </Button>
          <Button size="compact-xs" disabled={!name.trim()} onClick={() => {
            save([
              ...presets.filter(p => p.name !== name.trim()),
              { name: name.trim(), connectionId: global ? null : connectionId, layout: capture() },
            ]);
            setName("");
          }}>Save current</Button>
        </Group>

        <Table fz="xs" striped>
          <Table.Tbody>
            {visible.length === 0
              ? <Table.Tr><Table.Td><Text size="xs" c="dimmed">No presets yet.</Text></Table.Td></Table.Tr>
              : visible.map(preset => (
                <Table.Tr key={preset.name}>
                  <Table.Td>{preset.name}</Table.Td>
                  <Table.Td>
                    <Text size="10px" c="dimmed">
                      {preset.connectionId === null ? "every connection" : "this connection"}
                    </Text>
                  </Table.Td>
                  <Table.Td w={120}>
                    <Group gap={4} justify="flex-end">
                      <Button size="compact-xs" variant="default"
                        onClick={() => { apply(preset.layout); onClose(); }}>Apply</Button>
                      <ActionIcon size="sm" variant="subtle" color="red"
                        aria-label={`Delete ${preset.name}`}
                        onClick={() => save(presets.filter(p => p.name !== preset.name))}>
                        <IconTrash size={13} />
                      </ActionIcon>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
          </Table.Tbody>
        </Table>

        {/* The way back from a layout with every panel closed. */}
        <Group justify="space-between">
          <Text size="xs" c="dimmed">Lost a panel? Reset restores the default arrangement.</Text>
          <Button size="xs" variant="light" color="red"
            onClick={() => { reset(); onClose(); }}>Reset layout</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
