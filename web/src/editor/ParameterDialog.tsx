import { useEffect, useState } from "react";
import { Button, Group, Modal, Stack, Text, TextInput } from "@mantine/core";

/// Asked once per run, pre-filled with the last values for this tab: re-running the same query
/// with a different id is the common case, and retyping every parameter would be the annoyance.
export function ParameterDialog({ names, initial, onCancel, onRun }: {
  names: string[] | null;
  initial: Record<string, string>;
  onCancel: () => void;
  onRun: (values: Record<string, string>) => void;
}) {
  const [values, setValues] = useState<Record<string, string>>(initial);

  useEffect(() => { setValues(initial); }, [initial, names]);

  return (
    <Modal opened={names !== null} onClose={onCancel} title="Query parameters" size="sm">
      <Stack gap="xs">
        {names?.length === 0
          ? <Text size="sm">This statement has no parameters.</Text>
          : names?.map((name, index) => (
              <TextInput key={name} size="xs" label={name} data-autofocus={index === 0}
                value={values[name] ?? ""}
                onChange={e => setValues(v => ({ ...v, [name]: e.currentTarget.value }))}
                onKeyDown={e => { if (e.key === "Enter") onRun(values); }} />
            ))}

        <Group justify="flex-end" mt="xs">
          <Button size="xs" variant="default" onClick={onCancel}>Cancel</Button>
          <Button size="xs" onClick={() => onRun(values)}>Run</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
