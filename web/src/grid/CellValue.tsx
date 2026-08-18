import { Text } from "@mantine/core";

// NULL must never look like an empty string — a wrong read here costs the user real time.
export function CellValue({ value }: { value: unknown }) {
  if (value === null || value === undefined)
    return <Text component="span" size="xs" c="dimmed" fs="italic">NULL</Text>;
  if (value === "")
    return <Text component="span" size="xs" c="dimmed" title="empty string">&#x2205;</Text>;
  if (typeof value === "boolean")
    return <Text component="span" size="xs">{value ? "true" : "false"}</Text>;
  if (typeof value === "object")
    return <Text component="span" size="xs" ff="monospace">{JSON.stringify(value)}</Text>;
  return <Text component="span" size="xs">{String(value)}</Text>;
}
