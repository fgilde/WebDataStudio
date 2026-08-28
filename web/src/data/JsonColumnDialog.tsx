import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, CopyButton, Group, Loader, Modal, ScrollArea, Stack, Table, Text,
} from "@mantine/core";
import { jsonShape, type JsonShapeDto } from "../api";

/// What is actually inside a JSON column.
///
/// The grid shows one cell of text; this shows the shape: which paths exist, in how many of the
/// sampled documents, with which types. Two types at one path is the interesting case — that is
/// exactly where a flatten breaks — so it is a badge rather than a footnote.
export function JsonColumnDialog({ connectionId, objectRef, column, onClose, onFlatten }: {
  connectionId: string;
  objectRef: string;
  column: string;
  onClose: () => void;
  /// Opens SQL in a query tab: the whole flatten, or one path on its own.
  onFlatten?: (sql: string) => void;
}) {
  const [shape, setShape] = useState<JsonShapeDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    jsonShape(connectionId, objectRef, column)
      .then(value => { if (!cancelled) setShape(value); })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; };
  }, [connectionId, objectRef, column]);

  return (
    <Modal opened onClose={onClose} size="lg" title={`What is in ${column}`}>
      <Stack gap="sm">
        {error && <Alert color="red" variant="light">{error}</Alert>}
        {!shape && !error && <Loader size="sm" />}

        {shape && (
          <>
            <Group gap="xs">
              <Text size="xs" c="dimmed">
                {shape.parsed} of {shape.sampled} sampled rows read · {shape.paths.length} paths
              </Text>
              {shape.note && <Badge size="xs" color="orange">{shape.note}</Badge>}
            </Group>

            <ScrollArea h={320}>
              <Table striped highlightOnHover fz="xs" stickyHeader>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Path</Table.Th><Table.Th>Type</Table.Th>
                    <Table.Th>In</Table.Th><Table.Th>Example</Table.Th><Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {shape.paths.map(path => (
                    <Table.Tr key={path.path}>
                      <Table.Td><Text size="xs" ff="monospace">{path.path}</Text></Table.Td>
                      <Table.Td>
                        <Group gap={4}>
                          {path.types.map(type => (
                            // More than one type at a path is the thing worth seeing.
                            <Badge key={type} size="xs" variant="light"
                              color={path.types.length > 1 ? "orange" : "gray"}>
                              {type}
                            </Badge>
                          ))}
                        </Group>
                      </Table.Td>
                      <Table.Td>{path.present}/{shape.parsed}</Table.Td>
                      <Table.Td>
                        <Text size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>
                          {path.example ?? ""}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {/* The expression for this one path: copied into a query somebody is
                            already writing, which is what this is usually opened for. */}
                        <Group gap={4} wrap="nowrap">
                          <CopyButton value={path.expression}>
                            {({ copied, copy }) => (
                              <Button size="compact-xs" variant="subtle" onClick={copy}>
                                {copied ? "Copied" : "Copy SQL"}
                              </Button>
                            )}
                          </CopyButton>
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>

            {shape.flatten && (
              <Stack gap={4}>
                <Text size="xs" fw={600}>As columns</Text>
                <Code block fz="xs" style={{ maxHeight: 130, overflow: "auto" }}>
                  {shape.flatten}
                </Code>
                {onFlatten && (
                  <Group justify="flex-end">
                    <Button size="compact-xs"
                      onClick={() => { onFlatten(shape.flatten); onClose(); }}>
                      Open as a query
                    </Button>
                  </Group>
                )}
              </Stack>
            )}
          </>
        )}
      </Stack>
    </Modal>
  );
}
