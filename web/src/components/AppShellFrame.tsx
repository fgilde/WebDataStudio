import { useState } from "react";
import type { ReactNode } from "react";
import { ActionIcon, AppShell, Divider, Group, Text, Tooltip } from "@mantine/core";
import { IconCommand, IconDatabaseCog, IconLayoutBoard, IconPalette, IconTable } from "@tabler/icons-react";
import { Link, useLocation } from "react-router-dom";
import { ThemeDrawer } from "../ThemeDrawer";
import { ChatDock } from "../assist/ChatDock";
import { UserMenu } from "../auth/UserMenu";
import { McpButton } from "../mcp/McpButton";
import { BrandLinks } from "./BrandLinks";
import { ToolsMenu } from "../shell/ToolsMenu";
import { emit } from "../shell/bus";
import { useStudioTitle } from "./useStudioTitle";

export function AppShellFrame({ children }: { children: ReactNode }) {
  const [themeOpen, setThemeOpen] = useState(false);
  const { pathname } = useLocation();
  const title = useStudioTitle();

  return (
    <AppShell header={{ height: 44 }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="sm" justify="space-between" style={{ position: "relative" }}>
          <Group gap="sm">
            {/* The wordmark carries the product name, so no second text label next to it. */}
            <img src="/brand/logo.svg" alt="WebDataStudio" height={33}
              style={{ display: "block" }} />
            <Tooltip label="Studio">
              <ActionIcon component={Link} to="/" aria-label="Studio" variant={pathname === "/" ? "light" : "subtle"}>
                <IconTable size={17} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Connections">
              <ActionIcon component={Link} to="/connections" aria-label="Connections"
                variant={pathname === "/connections" ? "light" : "subtle"}>
                <IconDatabaseCog size={17} />
              </ActionIcon>
            </Tooltip>
          </Group>
          {/* The studio's own name, when it has one. Absolutely centred on the bar rather than
              placed between the two groups, whose widths differ. */}
          {title ? (
            <Text fw={700} tt="uppercase" c="dimmed" size="sm"
              style={{
                position: "absolute", left: 0, right: 0, textAlign: "center",
                letterSpacing: "0.16em", pointerEvents: "none",
              }}>
              {title}
            </Text>
          ) : null}

          <Group gap={2} wrap="nowrap">
            {/* Everything the studio can open, and the way to find it by typing. Up here rather
                than in the explorer: the explorer is a panel that can be closed, and losing the
                way back to every tool with it is exactly the wrong failure. */}
            {pathname === "/" ? (
              <>
                <ToolsMenu size="md" />
                <Tooltip label="Command palette (Ctrl+K)">
                  <ActionIcon variant="subtle" aria-label="Command palette"
                    onClick={() => emit("palette")}>
                    <IconCommand size={18} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label="Layout presets (Ctrl+L)">
                  <ActionIcon variant="subtle" aria-label="Layout presets"
                    onClick={() => emit("layouts")}>
                    <IconLayoutBoard size={18} />
                  </ActionIcon>
                </Tooltip>
              </>
            ) : null}
            <Tooltip label="Theme">
              <ActionIcon variant="subtle" aria-label="Theme" onClick={() => setThemeOpen(true)}>
                <IconPalette size={18} />
              </ActionIcon>
            </Tooltip>
            {/* Only when the studio actually is an MCP server. */}
            <McpButton />
            {/* Only on a studio with accounts; it renders nothing otherwise. */}
            <UserMenu />
            {/* What this studio does, and where it comes from, are two different kinds of link. */}
            <Divider orientation="vertical" my={12} mx={4} />
            <BrandLinks />
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main h="calc(100vh - 44px)">{children}</AppShell.Main>
      {/* In the corner, over everything, and absent unless assistance is configured. */}
      <ChatDock onUseStatement={sql => emit("use-sql", sql)} />
      <ThemeDrawer opened={themeOpen} onClose={() => setThemeOpen(false)} />
    </AppShell>
  );
}
