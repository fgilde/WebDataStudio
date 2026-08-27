import { Alert, Button, Group, List, Modal, Stack, Text } from "@mantine/core";
import type { SqlFindingDto } from "../api";

/// What the studio noticed in a statement, before it runs.
///
/// This is a warning and not a gate: an UPDATE over a whole table is a real thing to want, and a
/// studio that refused it would only teach people to work around the check. So the dialog says what
/// it saw and offers to run anyway — with the run being the plainly available button.
export function InspectionDialog({ findings, onRun, onCancel }: {
  findings: SqlFindingDto[];
  onRun: () => void;
  onCancel: () => void;
}) {
  return (
    <Modal opened onClose={onCancel} title="Before this runs" size="lg">
      <Stack gap="sm">
        {findings.map((finding, index) => (
          <Alert key={`${finding.id}-${index}`} color={finding.severity === "warning" ? "orange" : "blue"}
                 variant="light" title={finding.message}>
            <List size="xs" spacing={2}>
              <List.Item>statement {finding.statement}, line {finding.line}</List.Item>
              <List.Item>
                <Text size="xs" ff="monospace">{finding.excerpt}</Text>
              </List.Item>
            </List>
          </Alert>
        ))}

        <Text size="xs" c="dimmed">
          Nothing here is refused. Preferences can turn this reading off.
        </Text>

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onCancel}>Back to the editor</Button>
          <Button onClick={onRun}>Run anyway</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
