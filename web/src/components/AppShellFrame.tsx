import { useState } from "react";
import type { ReactNode } from "react";
import { ActionIcon, AppShell, Group, Text, Tooltip } from "@mantine/core";
import { IconDatabaseCog, IconPalette, IconTable } from "@tabler/icons-react";
import { Link, useLocation } from "react-router-dom";
import { ThemeDrawer } from "../ThemeDrawer";

export function AppShellFrame({ children }: { children: ReactNode }) {
  const [themeOpen, setThemeOpen] = useState(false);
  const { pathname } = useLocation();

  return (
    <AppShell header={{ height: 44 }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="sm" justify="space-between">
          <Group gap="sm">
            <Text fw={600}>WebDataStudio</Text>
            <Tooltip label="Studio">
              <ActionIcon component={Link} to="/" variant={pathname === "/" ? "light" : "subtle"}>
                <IconTable size={17} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Connections">
              <ActionIcon component={Link} to="/connections"
                variant={pathname === "/connections" ? "light" : "subtle"}>
                <IconDatabaseCog size={17} />
              </ActionIcon>
            </Tooltip>
          </Group>
          <Tooltip label="Theme">
            <ActionIcon variant="subtle" onClick={() => setThemeOpen(true)}><IconPalette size={18} /></ActionIcon>
          </Tooltip>
        </Group>
      </AppShell.Header>
      <AppShell.Main h="calc(100vh - 44px)">{children}</AppShell.Main>
      <ThemeDrawer opened={themeOpen} onClose={() => setThemeOpen(false)} />
    </AppShell>
  );
}
