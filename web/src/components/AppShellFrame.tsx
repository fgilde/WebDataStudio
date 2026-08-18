import { useState } from "react";
import type { ReactNode } from "react";
import { ActionIcon, AppShell, Group, Text, Tooltip } from "@mantine/core";
import { IconPalette } from "@tabler/icons-react";
import { ThemeDrawer } from "../ThemeDrawer";

export function AppShellFrame({ children }: { children: ReactNode }) {
  const [themeOpen, setThemeOpen] = useState(false);
  return (
    <AppShell header={{ height: 44 }} padding={0}>
      <AppShell.Header>
        <Group h="100%" px="sm" justify="space-between">
          <Text fw={600}>WebDataStudio</Text>
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
