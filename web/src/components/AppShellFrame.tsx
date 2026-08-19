import { useState } from "react";
import type { ReactNode } from "react";
import { ActionIcon, AppShell, Group, Text, Tooltip } from "@mantine/core";
import { IconDatabaseCog, IconPalette, IconTable } from "@tabler/icons-react";
import { Link, useLocation } from "react-router-dom";
import { ThemeDrawer } from "../ThemeDrawer";
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
            <img src="/brand/logo.svg" alt="WebDataStudio" height={24}
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
            <BrandLinks />
            <Tooltip label="Theme">
              <ActionIcon variant="subtle" aria-label="Theme" onClick={() => setThemeOpen(true)}>
                <IconPalette size={18} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main h="calc(100vh - 44px)">{children}</AppShell.Main>
      <ThemeDrawer opened={themeOpen} onClose={() => setThemeOpen(false)} />
    </AppShell>
  );
}
