import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Group, Loader, Progress, ScrollArea, Stack, Table, Text, Tooltip,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import {
  getMaskPolicy, profileObject, saveMaskPolicy, saveQualityRule,
  type ProfileDto, type QualityRuleDto,
} from "../api";

/// What a table actually holds, counted rather than guessed.
///
/// Between the health report — which reads the catalogue — and the data quality rules — which count
/// what breaks them — sits the question both assume somebody has answered: what is *in* this column.
/// One statement counts it, and the answers turn into rules and mask policies with one click, which
/// is the only reason to look at numbers like these.
export function ProfileTab({ connectionId, objectRef, table, schema }: {
  connectionId: string;
  objectRef: string;
  /// The table's own name and schema, for the rules a suggestion turns into.
  table: string;
  schema: string;
}) {
  const [profile, setProfile] = useState<ProfileDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;

    profileObject(connectionId, objectRef)
      .then(found => { if (!cancelled) { setProfile(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setProfile(null); setSaved([]); };
  }, [connectionId, objectRef]);

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!profile) return <Loader size="xs" m="xs" />;

  /// A suggestion, saved as a rule the Data quality tab then owns.
  const keep = (column: string, kind: string, argument: string | null, why: string) => {
    const rule: QualityRuleDto = {
      id: "", connectionId, schema, table, column,
      kind: kind as QualityRuleDto["kind"], argument,
      message: `${table}.${column}: ${why}`,
      enabled: true,
    };

    saveQualityRule(connectionId, rule)
      .then(() => {
        setSaved(current => [...current, `${column}:${kind}`]);
        notifications.show({ message: `rule kept: ${column} ${kind}` });
      })
      .catch(e => notifications.show({ color: "red", message: e.message }));
  };

  /// A column the values gave away, added to what the studio masks.
  const mask = (column: string) => {
    getMaskPolicy(connectionId)
      .then(policy => saveMaskPolicy(connectionId, {
        ...policy,
        extra: [...new Set([...policy.extra, column])],
      }))
      .then(() => notifications.show({ message: `${column} is masked from now on` }))
      .catch(e => notifications.show({ color: "red", message: e.message }));
  };

  return (
    <ScrollArea h="100%" p="xs">
      <Stack gap="sm">
        <Group gap="xs">
          <Text size="xs" fw={600}>{profile.rows.toLocaleString()} rows</Text>
          {profile.note && <Text size="xs" c="dimmed">{profile.note}</Text>}
        </Group>

        {profile.hints.length > 0 && (
          <Stack gap={4}>
            <Text size="xs" fw={600}>What the values look like</Text>
            {/* The masking heuristic reads names; this read the rows, which is how `col_17` is
                caught. */}
            {profile.hints.map(hint => (
              <Alert key={`${hint.column}-${hint.looks}`} color={hint.masked ? "gray" : "orange"}
                variant="light" p={6}>
                <Group gap="xs" justify="space-between" wrap="nowrap">
                  <Text size="xs">
                    <b>{hint.column}</b> looks like {hint.looks} — {hint.percent}% of{" "}
                    {hint.sampled} sampled rows
                  </Text>
                  {hint.masked
                    ? <Badge size="xs" color="gray">already masked</Badge>
                    : <Button size="compact-xs" variant="default" onClick={() => mask(hint.column)}>
                        Mask this column
                      </Button>}
                </Group>
              </Alert>
            ))}
          </Stack>
        )}

        <Table striped fz="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Column</Table.Th>
              <Table.Th w={120}>Empty</Table.Th>
              <Table.Th w={90}>Different</Table.Th>
              <Table.Th>Smallest</Table.Th>
              <Table.Th>Largest</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {profile.columns.map(column => (
              <Table.Tr key={column.name}>
                <Table.Td>
                  <Group gap={4} wrap="nowrap">
                    <Text size="xs">{column.name}</Text>
                    {column.unique && <Badge size="xs" variant="light">unique</Badge>}
                    {column.constant && <Badge size="xs" color="yellow" variant="light">one value</Badge>}
                    {column.masked && <Badge size="xs" color="gray" variant="light">masked</Badge>}
                  </Group>
                  <Text size="10px" c="dimmed">{column.dataType}</Text>
                </Table.Td>
                <Table.Td>
                  {column.nulls === 0
                    ? <Text size="xs" c="dimmed">none</Text>
                    : <Tooltip label={`${column.nulls} of ${profile.rows}`}>
                        <div>
                          <Progress value={column.nullPercent} size="sm"
                            color={column.nullPercent > 50 ? "orange" : "blue"} />
                          <Text size="10px" c="dimmed">{column.nullPercent}%</Text>
                        </div>
                      </Tooltip>}
                </Table.Td>
                <Table.Td>{column.distinct ?? "—"}</Table.Td>
                <Table.Td style={{ maxWidth: 160, overflow: "hidden", textOverflow: "ellipsis" }}>
                  {column.min ?? "—"}
                </Table.Td>
                <Table.Td style={{ maxWidth: 160, overflow: "hidden", textOverflow: "ellipsis" }}>
                  {column.max ?? "—"}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {profile.suggestions.length > 0 && (
          <Stack gap={4}>
            <Text size="xs" fw={600}>Rules these numbers suggest</Text>
            <Text size="xs" c="dimmed">
              What is true today, offered as a rule that says it has to stay true. Each one lands in
              Administration → Data quality, where it can be changed or switched off.
            </Text>
            {profile.suggestions.map(suggestion => {
              const key = `${suggestion.column}:${suggestion.kind}`;

              return (
                <Group key={key} gap="xs" wrap="nowrap">
                  <Button size="compact-xs" variant="default"
                    disabled={saved.includes(key)}
                    onClick={() => keep(suggestion.column, suggestion.kind, suggestion.argument,
                      suggestion.why)}>
                    {saved.includes(key) ? "kept" : "Keep as a rule"}
                  </Button>
                  <Text size="xs">
                    <b>{suggestion.column}</b> {label(suggestion.kind, suggestion.argument)} —{" "}
                    <Text span c="dimmed">{suggestion.why}</Text>
                  </Text>
                </Group>
              );
            })}
          </Stack>
        )}
      </Stack>
    </ScrollArea>
  );
}

function label(kind: string, argument: string | null) {
  switch (kind) {
    case "NotNull": return "always has a value";
    case "Unique": return "has no duplicates";
    case "Range": return `stays between ${argument}`;
    default: return kind;
  }
}
