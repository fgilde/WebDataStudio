import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Select, Stack, Switch, Table, Text, TextInput,
  Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import {
  deleteQualityRule, qualityRules, runQualityRules, saveQualityRule,
  type QualityResultDto, type QualityRuleDto,
} from "../api";

/// What each kind needs written in the argument box, and what it means. The placeholder is the
/// documentation people actually read.
const KINDS: Record<QualityRuleDto["kind"], { label: string; argument: string | null }> = {
  NotNull: { label: "Has a value", argument: null },
  Unique: { label: "No duplicates", argument: null },
  Range: { label: "Between two numbers", argument: "0..100" },
  Referential: { label: "Points at a row that exists", argument: "customers.id" },
  Freshness: { label: "Newest value is recent", argument: "24h" },
  Expression: { label: "My own condition", argument: "total < 0" },
};

const empty = (connectionId: string): QualityRuleDto => ({
  id: "", connectionId, schema: "", table: "", column: "",
  kind: "NotNull", argument: null, message: null, enabled: true,
});

/// Rules about the data rather than about the schema.
///
/// The health report reads the catalogue and can say "this table has no primary key". It cannot say
/// "a third of yesterday's orders have no customer", because that is in the rows. A rule here is one
/// counting query, so its answer is one number — and a number is something the alert sweep can watch.
export function Quality({ connectionId, onOpenInEditor }: {
  connectionId: string;
  /// The counting statement itself, where somebody wants to see which rows those are.
  onOpenInEditor?: (sql: string) => void;
}) {
  const [rules, setRules] = useState<QualityRuleDto[] | null>(null);
  const [results, setResults] = useState<QualityResultDto[] | null>(null);
  const [draft, setDraft] = useState<QualityRuleDto>(() => empty(connectionId));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Another connection is another set of rules: the panel is keyed on the connection, so the draft
  // and the last run go with it rather than being cleared here.
  useEffect(() => {
    qualityRules(connectionId).then(setRules).catch(e => setError(e.message));
  }, [connectionId]);

  const reload = () => qualityRules(connectionId).then(setRules).catch(e => setError(e.message));

  const add = () => {
    setError(null);
    saveQualityRule(connectionId, { ...draft, connectionId })
      .then(() => { setDraft(empty(connectionId)); return reload(); })
      .catch(e => setError(e.message));
  };

  const run = () => {
    setBusy(true);
    setError(null);
    runQualityRules(connectionId)
      .then(report => setResults(report.results))
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  const resultFor = (id: string) => results?.find(result => result.rule.id === id);
  const argument = KINDS[draft.kind].argument;

  if (rules === null && error === null) return <Loader size="xs" m="sm" />;

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6} align="flex-end">
        <TextInput size="xs" w={110} label="Schema" placeholder="public" value={draft.schema}
          onChange={e => setDraft({ ...draft, schema: e.currentTarget.value })} />
        <TextInput size="xs" w={130} label="Table" value={draft.table}
          onChange={e => setDraft({ ...draft, table: e.currentTarget.value })} />
        <TextInput size="xs" w={130} label="Column" value={draft.column}
          onChange={e => setDraft({ ...draft, column: e.currentTarget.value })} />
        <Select size="xs" w={190} label="Rule" allowDeselect={false} value={draft.kind}
          data={Object.entries(KINDS).map(([value, kind]) => ({ value, label: kind.label }))}
          onChange={value => setDraft({
            ...draft,
            kind: (value ?? "NotNull") as QualityRuleDto["kind"],
            // The argument belongs to the kind: keeping `0..100` on a freshness rule would only be
            // an error later.
            argument: null,
          })} />
        {argument !== null && (
          <TextInput size="xs" w={140} label="Argument" placeholder={argument}
            value={draft.argument ?? ""}
            onChange={e => setDraft({ ...draft, argument: e.currentTarget.value })} />
        )}
        <TextInput size="xs" flex={1} label="Message" placeholder="every order needs a customer"
          value={draft.message ?? ""}
          onChange={e => setDraft({ ...draft, message: e.currentTarget.value })} />
        <Button size="compact-xs" leftSection={<IconPlus size={13} />}
          disabled={!draft.table || (argument !== null && !draft.argument)} onClick={add}>
          Add rule
        </Button>
      </Group>

      {error && <Alert color="red" variant="light">{error}</Alert>}

      <Group gap="xs">
        <Button size="compact-xs" variant="default" loading={busy}
          disabled={!rules?.some(rule => rule.enabled)} onClick={run}>
          Run now
        </Button>
        {results !== null && (
          <Text size="xs" c="dimmed">
            {results.filter(result => result.violations > 0 || result.error).length === 0
              ? "everything passed"
              : `${results.filter(result => result.violations > 0).length} failing`}
          </Text>
        )}
      </Group>

      {rules?.length === 0 ? (
        <Text size="xs" c="dimmed">
          No rules yet. A rule counts the rows that break it, and a failing rule becomes an alert
          alongside the health findings.
        </Text>
      ) : (
        <Table striped fz="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Table</Table.Th><Table.Th>Column</Table.Th><Table.Th>Rule</Table.Th>
              <Table.Th>Result</Table.Th><Table.Th w={60}>On</Table.Th><Table.Th w={40} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rules?.map(rule => {
              const result = resultFor(rule.id);

              return (
                <Table.Tr key={rule.id}>
                  <Table.Td>{rule.schema ? `${rule.schema}.${rule.table}` : rule.table}</Table.Td>
                  <Table.Td>{rule.column || "—"}</Table.Td>
                  <Table.Td>
                    {KINDS[rule.kind].label}
                    {rule.argument ? <Text span c="dimmed"> ({rule.argument})</Text> : null}
                    {/* The sentence somebody wrote is the point of the rule; a table that only
                        shows it when the rule fails hides what the rule is for. */}
                    {rule.message
                      ? <Text size="10px" c="dimmed">{rule.message}</Text>
                      : null}
                  </Table.Td>
                  <Table.Td>
                    {result === undefined ? <Text c="dimmed">not run</Text>
                      : result.error ? <Badge size="xs" color="gray">{result.error}</Badge>
                      : result.violations === 0 ? <Badge size="xs" color="green">ok</Badge>
                      : (
                        <Group gap={4} wrap="nowrap">
                          <Badge size="xs" color="red">{result.violations} rows</Badge>
                          {onOpenInEditor && (
                            <Tooltip label="Open the counting statement">
                              <Button size="compact-xs" variant="subtle"
                                onClick={() => onOpenInEditor(result.statement)}>
                                Show
                              </Button>
                            </Tooltip>
                          )}
                        </Group>
                      )}
                  </Table.Td>
                  <Table.Td>
                    <Switch size="xs" checked={rule.enabled}
                      aria-label={`Enable ${rule.table}.${rule.column} ${rule.kind}`}
                      onChange={e => saveQualityRule(connectionId,
                        { ...rule, enabled: e.currentTarget.checked })
                        .then(reload).catch(err => setError(err.message))} />
                  </Table.Td>
                  <Table.Td>
                    <ActionIcon size="sm" variant="subtle" color="red"
                      aria-label={`Delete rule for ${rule.table}`}
                      onClick={() => deleteQualityRule(connectionId, rule.id)
                        .then(reload).catch(err => setError(err.message))}>
                      <IconTrash size={14} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      )}

      <Text size="xs" c="dimmed">
        Each rule is one counting query against this connection, and a failing one is reported with
        the health findings — so a rule written once is watched from then on.
      </Text>
    </Stack>
  );
}
