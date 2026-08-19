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

/// The presets that apply to the connection in front of the user, in the order the slot numbers
/// follow: the shortcut and the list have to agree on what "3" means.
export const visiblePresets = (presets: LayoutPreset[], connectionId: string | null) =>
  presets.filter(p => p.connectionId === null || p.connectionId === connectionId);

/// Slots are 1-based, so Ctrl+L 1 is the first entry in the list. Slot 0 is the reset, handled by
/// the caller.
export const presetForSlot = (
  presets: LayoutPreset[], connectionId: string | null, slot: number,
): LayoutPreset | undefined => visiblePresets(presets, connectionId)[slot - 1];

export function LayoutPresetsModal({
  opened, onClose, connectionId, capture, apply, reset, presets, save, slotsArmed = false,
}: {
  opened: boolean;
  onClose: () => void;
  connectionId: string | null;
  capture: () => unknown;
  apply: (layout: unknown) => void;
  reset: () => void;
  presets: LayoutPreset[];
  save: (list: LayoutPreset[]) => void;
  /// True while the Ctrl+L chord is still listening. The numbers are only true for those seconds,
  /// so showing them the rest of the time would promise a key that does nothing.
  slotsArmed?: boolean;
}) {
  const [name, setName] = useState("");
  const [global, setGlobal] = useState(false);

  const visible = visiblePresets(presets, connectionId);

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
              : visible.map((preset, at) => (
                <Table.Tr key={preset.name}>
                  <Table.Td w={30}>
                    {/* The slot the Ctrl+L chord applies; past nine there is no key left to press. */}
                    {slotsArmed && at < 9
                      ? <Text size="sm" fw={700} c="orange" aria-label={`Slot ${at + 1}`}>
                          {at + 1}
                        </Text>
                      : null}
                  </Table.Td>
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
          <Text size="xs" c={slotsArmed ? "orange" : "dimmed"}>
            {slotsArmed
              ? "Press 1…9 for a preset, 0 for the default."
              : "Ctrl+L then 1…9 applies a preset, Ctrl+L then 0 resets."}
          </Text>
          <Button size="xs" variant="light" color="red"
            onClick={() => { reset(); onClose(); }}>Reset layout</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
