import { useState } from "react";
import type { ReactNode } from "react";
import { ActionIcon, AppShell, Divider, Group, Text, Tooltip } from "@mantine/core";
import { IconDatabaseCog, IconLayoutBoard, IconPalette, IconTable } from "@tabler/icons-react";
import { Link, useLocation } from "react-router-dom";
import { ThemeDrawer } from "../ThemeDrawer";
import { UserMenu } from "../auth/UserMenu";
import { McpButton } from "../mcp/McpButton";
import { BrandLinks } from "./BrandLinks";
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
            {/* The explorer holds the same button, but the explorer itself can be closed — and a
                lost layout is exactly when you need this. */}
            {pathname === "/" ? (
              <Tooltip label="Layout presets (Ctrl+L)">
                <ActionIcon variant="subtle" aria-label="Layout presets"
                  onClick={() => document.dispatchEvent(new CustomEvent("wds:layouts"))}>
                  <IconLayoutBoard size={18} />
                </ActionIcon>
              </Tooltip>
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
      <ThemeDrawer opened={themeOpen} onClose={() => setThemeOpen(false)} />
    </AppShell>
  );
}
