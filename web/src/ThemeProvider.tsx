import { createContext, useContext, useEffect, useState, useCallback } from "react";
import type { ReactNode } from "react";
import { MantineProvider } from "@mantine/core";
import { ModalsProvider } from "@mantine/modals";
import { Notifications } from "@mantine/notifications";
import "@mantine/notifications/styles.css";
import "@mantine/spotlight/styles.css";
import { THEMES, DEFAULT_THEME, THEME_KEY, getTheme, type AppTheme } from "./themes";

interface ThemeCtx {
  themeId: string;
  setThemeId: (id: string) => void;
  current: AppTheme;
  themes: AppTheme[];
}
const Ctx = createContext<ThemeCtx | null>(null);

// App theme context that switches at runtime while maintaining per-theme color schemes.
export function AppThemeProvider({ children }: { children: ReactNode }) {
  const [themeId, setThemeIdState] = useState<string>(() => localStorage.getItem(THEME_KEY) ?? DEFAULT_THEME);
  const setThemeId = useCallback((id: string) => { localStorage.setItem(THEME_KEY, id); setThemeIdState(id); }, []);
  const current = getTheme(themeId);

  // The command palette cycles themes without knowing the list; this is the one place that does.
  useEffect(() => {
    const cycle = () => setThemeIdState(id => {
      const index = THEMES.findIndex(t => t.id === id);
      const next = THEMES[(index + 1) % THEMES.length].id;
      localStorage.setItem(THEME_KEY, next);
      return next;
    });

    document.addEventListener("wds:cycle-theme", cycle);
    return () => document.removeEventListener("wds:cycle-theme", cycle);
  }, []);

  return (
    <Ctx.Provider value={{ themeId, setThemeId, current, themes: THEMES }}>
      <MantineProvider theme={current.theme} forceColorScheme={current.scheme}>
        <Notifications position="top-right" />
        <ModalsProvider>{children}</ModalsProvider>
      </MantineProvider>
    </Ctx.Provider>
  );
}

export function useAppTheme(): ThemeCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error("useAppTheme outside AppThemeProvider");
  return c;
}
