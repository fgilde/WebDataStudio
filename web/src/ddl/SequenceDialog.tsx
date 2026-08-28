import { Button, Checkbox, Group, Modal, NumberInput, Stack, Text, TextInput } from "@mantine/core";
import { useEffect, useState } from "react";
import { previewSequence } from "../api";
import type { PendingScript } from "./ScriptConfirm";
import { notifications } from "@mantine/notifications";

export interface SequenceTarget {
  connectionId: string;
  schema: string;
  /// The sequence that exists. Absent means a new one.
  name?: string;
}

/// A sequence, created or changed.
///
/// The one people actually come here for is **restart**: an import wrote its own ids, the sequence
/// kept counting from where it was, and the next insert collides. Everything left empty stays the
/// engine's own default rather than being guessed at.
export function SequenceDialog({ target, onClose, onPreview }: {
  target: SequenceTarget | null;
  onClose: () => void;
  onPreview: (pending: PendingScript) => void;
}) {
  const [name, setName] = useState("");
  const [start, setStart] = useState<number | string>("");
  const [increment, setIncrement] = useState<number | string>("");
  const [minValue, setMin] = useState<number | string>("");
  const [maxValue, setMax] = useState<number | string>("");
  const [cache, setCache] = useState<number | string>("");
  const [restartWith, setRestart] = useState<number | string>("");
  const [cycle, setCycle] = useState(false);

  useEffect(() => {
    if (!target) return;

    setName(target.name ?? "");
    setStart("");
    setIncrement("");
    setMin("");
    setMax("");
    setCache("");
    setRestart("");
    setCycle(false);
  }, [target]);

  const number = (value: number | string) =>
    typeof value === "number" ? value : value.trim() === "" ? null : Number(value);

  const save = async () => {
    if (!target) return;

    const objectName = (target.name ?? name).trim();
    if (objectName.length === 0) {
      notifications.show({ color: "red", message: "this needs a name" });
      return;
    }

    try {
      const preview = await previewSequence(target.connectionId, {
        schema: target.schema,
        name: objectName,
        create: !target.name,
        start: number(start),
        increment: number(increment),
        minValue: number(minValue),
        maxValue: number(maxValue),
        cache: number(cache),
        restartWith: target.name ? number(restartWith) : null,
        cycle,
      });

      onPreview({
        connectionId: target.connectionId,
        title: `${target.name ? "Change" : "Create"} sequence ${objectName}`,
        ...preview,
      });
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    }
  };

  return (
    <Modal opened={target !== null} onClose={onClose}
      title={target?.name ? `Sequence ${target.name}` : "New sequence"}>
      <Stack gap="xs">
        {!target?.name && (
          <TextInput size="xs" label="Name" value={name} data-autofocus
            onChange={event => setName(event.currentTarget.value)} />
        )}

        {target?.name && (
          <>
            <NumberInput size="xs" label="Restart with" value={restartWith} onChange={setRestart}
              placeholder="leave empty to keep counting" />
            <Text size="10px" c="dimmed">
              The answer to an import that wrote its own ids: set this above the largest one in use.
            </Text>
          </>
        )}

        <Group grow>
          {!target?.name && (
            <NumberInput size="xs" label="Start with" value={start} onChange={setStart}
              placeholder="engine default" />
          )}
          <NumberInput size="xs" label="Increment by" value={increment} onChange={setIncrement}
            placeholder="1" />
        </Group>

        <Group grow>
          <NumberInput size="xs" label="Minimum" value={minValue} onChange={setMin} placeholder="none" />
          <NumberInput size="xs" label="Maximum" value={maxValue} onChange={setMax} placeholder="none" />
        </Group>

        <Group grow align="flex-end">
          <NumberInput size="xs" label="Cache" value={cache} onChange={setCache} placeholder="engine default" />
          <Checkbox size="xs" label="Start over at the minimum when it runs out" checked={cycle}
            onChange={event => setCycle(event.currentTarget.checked)} />
        </Group>

        <Group justify="flex-end" mt="xs">
          <Button variant="default" size="xs" onClick={onClose}>Cancel</Button>
          <Button size="xs" onClick={save}>Show the statement…</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
