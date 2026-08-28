import { Text } from "@mantine/core";
import { formatTimestamp, looksTimestamp } from "./formatTime";
import { preferences } from "../shell/preferences";

// NULL must never look like an empty string — a wrong read here costs the user real time.
export function CellValue({ value }: { value: unknown }) {
  if (value === null || value === undefined)
    return <Text component="span" size="xs" c="dimmed" fs="italic">NULL</Text>;
  if (value === "")
    return <Text component="span" size="xs" c="dimmed" title="empty string">&#x2205;</Text>;
  if (typeof value === "boolean")
    return <Text component="span" size="xs">{value ? "true" : "false"}</Text>;

  // A timestamp is read by a person, not by a parser: no T in the middle, no seven decimal places,
  // and on the clock they chose. The raw value stays one hover away, because "which one is it
  // really" is exactly the question this is about.
  if (looksTimestamp(value)) {
    const shown = formatTimestamp(value, preferences().timeZone);

    return (
      <Text component="span" size="xs" title={`${value}${shown.zoned ? "" : " (no time zone)"}`}
        style={shown.zoned ? undefined : { borderBottom: "1px dotted currentColor" }}>
        {shown.text}
      </Text>
    );
  }
  if (typeof value === "object")
    return <Text component="span" size="xs" ff="monospace">{JSON.stringify(value)}</Text>;
  return <Text component="span" size="xs">{String(value)}</Text>;
}
