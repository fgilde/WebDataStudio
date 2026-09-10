import { useState } from "react";
import { ActionIcon, Badge, Group, Loader, Menu, Paper, Text, Tooltip } from "@mantine/core";
import {
  IconCopy, IconDotsVertical, IconExternalLink, IconPencil, IconRefresh, IconTable, IconTrash,
} from "@tabler/icons-react";
import { isChart, type Dashboard, type Widget } from "../model";
import { useWidgetData } from "../useWidgetData";
import { WidgetBody } from "./WidgetBody";

/// One widget: its title, its rows, and the four things somebody does to it.
///
/// The frame owns everything that is the same for all fifteen types — the loading state, the error,
/// the menu, and the switch to the rows the picture was made of. A type only has to draw.
export function WidgetFrame({ widget, dashboard, chosen, dark, editing, nonce, onEdit, onDuplicate,
  onRemove, onOpenInEditor }: {
  widget: Widget;
  dashboard: Dashboard;
  chosen: Record<string, string[]>;
  dark: boolean;
  editing: boolean;
  nonce: number;
  onEdit?: () => void;
  onDuplicate?: () => void;
  onRemove?: () => void;
  onOpenInEditor?: (connectionId: string, sql: string) => void;
}) {
  const [asRows, setAsRows] = useState(false);
  const [own, setOwn] = useState(0);
  const data = useWidgetData(widget, dashboard, chosen, nonce + own);

  const rowsToggle = isChart(widget.type);

  if (widget.type === "Row")
    return (
      <Group gap="xs" h="100%" px={4} wrap="nowrap"
        style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
        <Text fw={700} size="sm" tt="uppercase" c="dimmed" style={{ letterSpacing: "0.08em" }}>
          {widget.title}
        </Text>
        {editing ? (
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon size="xs" variant="subtle" aria-label={`Menu for ${widget.title}`}>
                <IconDotsVertical size={13} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<IconPencil size={14} />} onClick={onEdit}>Settings</Menu.Item>
              <Menu.Item leftSection={<IconTrash size={14} />} c="red" onClick={onRemove}>
                Delete
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        ) : null}
      </Group>
    );

  return (
    <Paper withBorder radius="md" h="100%" style={{ display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <Group gap={4} px={8} py={4} justify="space-between" wrap="nowrap"
        className="wds-widget-handle"
        style={{ cursor: editing ? "move" : undefined, minHeight: 28 }}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          {widget.description ? (
            <Tooltip label={widget.description} multiline w={240}>
              <Text size="xs" fw={600} truncate>{widget.title}</Text>
            </Tooltip>
          ) : (
            <Text size="xs" fw={600} truncate>{widget.title}</Text>
          )}
          {widget.source.kind === "Federated" ? (
            <Tooltip label="Several connections, staged and joined by the studio">
              <Badge size="xs" variant="light" color="grape">federated</Badge>
            </Tooltip>
          ) : null}
          {data.truncated ? (
            <Tooltip label="The row cap cut this result; the picture is of what came back">
              <Badge size="xs" variant="light" color="orange">capped</Badge>
            </Tooltip>
          ) : null}
        </Group>

        <Group gap={2} wrap="nowrap">
          {data.running ? <Loader size={12} /> : null}
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon size="xs" variant="subtle" aria-label={`Menu for ${widget.title}`}>
                <IconDotsVertical size={13} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<IconRefresh size={14} />} onClick={() => setOwn(n => n + 1)}>
                Run again
              </Menu.Item>
              {rowsToggle ? (
                // The accessibility relief and the debugging tool in one switch.
                <Menu.Item leftSection={<IconTable size={14} />} onClick={() => setAsRows(one => !one)}>
                  {asRows ? "Show the chart" : "Show the rows"}
                </Menu.Item>
              ) : null}
              {onOpenInEditor && widget.source.connectionId && widget.source.sql ? (
                <Menu.Item leftSection={<IconExternalLink size={14} />}
                  onClick={() => onOpenInEditor(widget.source.connectionId!, widget.source.sql!)}>
                  Open in the editor
                </Menu.Item>
              ) : null}
              {editing ? <Menu.Divider /> : null}
              {editing ? (
                <Menu.Item leftSection={<IconPencil size={14} />} onClick={onEdit}>Settings</Menu.Item>
              ) : null}
              {editing ? (
                <Menu.Item leftSection={<IconCopy size={14} />} onClick={onDuplicate}>Duplicate</Menu.Item>
              ) : null}
              {editing ? (
                <Menu.Item leftSection={<IconTrash size={14} />} c="red" onClick={onRemove}>
                  Delete
                </Menu.Item>
              ) : null}
            </Menu.Dropdown>
          </Menu>
        </Group>
      </Group>

      <div style={{ flex: 1, minHeight: 0, padding: 4 }}>
        {data.error ? (
          // One broken widget says why, in its own frame, and the page around it keeps working.
          <Text size="xs" c="red" p={4} style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
            {data.error}
          </Text>
        ) : (
          <WidgetBody widget={widget} data={data} dashboard={dashboard} chosen={chosen} dark={dark}
            asRows={asRows} />
        )}
      </div>
    </Paper>
  );
}
