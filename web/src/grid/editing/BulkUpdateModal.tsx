import { useState } from "react";
import { Button, Group, Modal, NumberInput, Select, Stack, Switch, Table, Text, TextInput } from "@mantine/core";
import { applyMacro, macroError, type Macro } from "./applyMacro";

const KINDS = [
  { value: "set", label: "Set a fixed value" },
  { value: "null", label: "Set NULL" },
  { value: "trim", label: "Trim whitespace" },
  { value: "upper", label: "Upper case" },
  { value: "lower", label: "Lower case" },
  { value: "replace", label: "Find and replace" },
  { value: "add", label: "Add to number" },
  { value: "template", label: "Template ({value}, {row})" },
];

/// Applies one transformation to every selected cell. The first ten results are shown next to
/// their originals before anything enters the change set.
export function BulkUpdateModal({ values, onApply, onClose }: {
  values: { rowIndex: number; column: string; value: unknown }[] | null;
  onApply: (transformed: { rowIndex: number; column: string; value: unknown }[]) => void;
  onClose: () => void;
}) {
  const [kind, setKind] = useState<Macro["kind"]>("set");
  const [text, setText] = useState("");
  const [replacement, setReplacement] = useState("");
  const [regex, setRegex] = useState(false);
  const [amount, setAmount] = useState<number | "">(1);

  if (!values) return null;

  const macro: Macro =
    kind === "set" ? { kind, value: text }
    : kind === "replace" ? { kind, find: text, with: replacement, regex }
    : kind === "add" ? { kind, amount: typeof amount === "number" ? amount : 0 }
    : kind === "template" ? { kind, pattern: text || "{value}" }
    : { kind } as Macro;

  const error = macroError(macro);
  const preview = values.slice(0, 10).map(v => ({
    ...v, next: applyMacro(v.value, macro, v.rowIndex),
  }));

  return (
    <Modal opened onClose={onClose} title={`Bulk update (${values.length} cells)`} size="lg">
      <Stack gap="sm">
        <Select label="Operation" data={KINDS} value={kind}
          onChange={v => v && setKind(v as Macro["kind"])} />

        {(kind === "set" || kind === "template") && (
          <TextInput label={kind === "set" ? "Value" : "Pattern"} value={text}
            onChange={e => setText(e.currentTarget.value)} />
        )}

        {kind === "replace" && (
          <>
            <Group grow>
              <TextInput label="Find" value={text} onChange={e => setText(e.currentTarget.value)} />
              <TextInput label="Replace with" value={replacement}
                onChange={e => setReplacement(e.currentTarget.value)} />
            </Group>
            <Switch label="Regular expression" checked={regex}
              onChange={e => setRegex(e.currentTarget.checked)} />
          </>
        )}

        {kind === "add" && (
          <NumberInput label="Amount" value={amount}
            onChange={v => setAmount(typeof v === "number" ? v : "")} />
        )}

        {error && <Text c="red" size="sm">{error}</Text>}

        <Text size="sm" fw={600}>Preview</Text>
        <Table fz="xs" withTableBorder>
          <Table.Thead>
            <Table.Tr><Table.Th>Column</Table.Th><Table.Th>Before</Table.Th><Table.Th>After</Table.Th></Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {preview.map((p, i) => (
              <Table.Tr key={i}>
                <Table.Td>{p.column}</Table.Td>
                <Table.Td>{String(p.value ?? "NULL")}</Table.Td>
                <Table.Td>{String(p.next ?? "NULL")}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button disabled={error !== null}
            onClick={() => {
              onApply(values.map(v => ({ ...v, value: applyMacro(v.value, macro, v.rowIndex) })));
              onClose();
            }}>
            Apply to change set
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
