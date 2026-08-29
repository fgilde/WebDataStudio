import {
  ActionIcon, Menu, Text, Tooltip,
} from "@mantine/core";
import {
  IconArchive, IconArrowsJoin, IconBinaryTree, IconBookmarks, IconGitCompare, IconHeartRateMonitor,
  IconHistory, IconKey, IconNotebook, IconSettingsCog, IconSitemap, IconStopwatch, IconTable,
  IconTools, IconWaveSine, IconZoomCode,
} from "@tabler/icons-react";
import { emit, useShellSnapshot } from "./bus";
import { visibleTools, type ToolDefinition } from "./tools";

/// One icon per tool. A `Record` keyed by the tool id rather than a lookup with a fallback: adding a
/// tool without an icon is then a compile error instead of a grey square.
const ICONS: Record<string, React.ReactNode> = {
  "tool.datasearch": <IconZoomCode size={15} />,
  "tool.diagram": <IconSitemap size={15} />,
  "tool.builder": <IconTable size={15} />,
  "tool.notebook": <IconNotebook size={15} />,
  "tool.perspective": <IconBinaryTree size={15} />,
  "tool.compare": <IconGitCompare size={15} />,
  "tool.federate": <IconArrowsJoin size={15} />,
  "tool.archive": <IconArchive size={15} />,
  "tool.redis": <IconKey size={15} />,
  "tool.admin": <IconSettingsCog size={15} />,
  "tool.jobs": <IconStopwatch size={15} />,
  "tool.capture": <IconWaveSine size={15} />,
  "tool.health": <IconHeartRateMonitor size={15} />,
  "tool.history": <IconHistory size={15} />,
  "tool.saved": <IconBookmarks size={15} />,
};

/// Every tool behind one menu, with its name.
///
/// This used to be a row of thirteen 15-pixel icons in the narrowest column of the app, and it was
/// cut off at the panel's edge. Names also solve what icons could not: `IconTable` was both "Studio"
/// in the header and "Query builder" in the explorer, and nobody guesses that a binary tree means
/// "a row and everything related to it".
export function ToolsMenu({ size = "sm", label = "Tools" }: {
  size?: "sm" | "md";
  label?: string;
}) {
  const shell = useShellSnapshot();
  const tools = visibleTools({ admin: shell.admin, engine: shell.engine });

  // Grouped the way somebody looks for them: what you do with the data, then what you do with the
  // server, then the panels that are always there.
  const named: [string, string[]][] = [
    ["Data", ["tool.datasearch", "tool.diagram", "tool.builder", "tool.notebook",
      "tool.perspective", "tool.compare", "tool.federate", "tool.archive", "tool.redis"]],
    ["Server", ["tool.admin", "tool.jobs", "tool.capture"]],
    ["Panels", ["tool.dashboard", "tool.health", "tool.history", "tool.saved", "tool.reports"]],
  ];

  // Anything a group forgot still shows up. These two lists used to be independent, and a tool
  // nobody added to a group here was simply invisible — which is exactly what happened to the
  // dashboard and to the reports page.
  const placed = new Set(named.flatMap(([, ids]) => ids));
  const rest = tools.filter(tool => !placed.has(tool.id));

  const groups: [string, ToolDefinition[]][] = [
    ...named.map<[string, ToolDefinition[]]>(([name, ids]) =>
      [name, tools.filter(tool => ids.includes(tool.id))]),
    ...(rest.length > 0 ? [["More", rest] as [string, ToolDefinition[]]] : []),
  ];

  return (
    <Menu shadow="md" width={330} position="bottom-end">
      <Menu.Target>
        <Tooltip label="Tools">
          <ActionIcon size={size} variant="subtle" aria-label={label}>
            <IconTools size={size === "sm" ? 15 : 18} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>

      <Menu.Dropdown>
        {groups.map(([name, entries]) => entries.length === 0 ? null : (
          <div key={name}>
            <Menu.Label>{name}</Menu.Label>
            {entries.map(tool => (
              <Menu.Item key={tool.id} leftSection={ICONS[tool.id]}
                disabled={tool.requiresConnection === true && !shell.activeConnection}
                rightSection={tool.shortcut
                  ? <Text size="xs" c="dimmed">{tool.shortcut}</Text>
                  : undefined}
                // The dock owns the tools; the header only says which one somebody asked for.
                onClick={() => emit("command", tool.id)}>
                {tool.label}
              </Menu.Item>
            ))}
          </div>
        ))}

        <Menu.Divider />
        <Menu.Label>Everything else</Menu.Label>
        <Menu.Item rightSection={<Text size="xs" c="dimmed">Ctrl+K</Text>}
          onClick={() => emit("palette")}>
          Command palette
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
