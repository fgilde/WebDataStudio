import { createContext, useContext, useEffect, useState, useCallback } from "react";
import type { ReactNode } from "react";
import { MantineProvider } from "@mantine/core";
import { ModalsProvider } from "@mantine/modals";
import { Notifications } from "@mantine/notifications";
import "@mantine/notifications/styles.css";
import "@mantine/spotlight/styles.css";
import { THEMES, DEFAULT_THEME, THEME_KEY, getTheme, type AppTheme } from "./themes";
import { onShell } from "./shell/bus";
import { me } from "./api";

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

  // A deployment can say which theme to start in (WDS_THEME, or WithTheme in Aspire). It is a
  // starting point, not a lock: a person's own choice lives in localStorage and wins, and this
  // never writes there — so raising the deployment's default later still reaches everybody who
  // never picked one.
  useEffect(() => {
    if (localStorage.getItem(THEME_KEY)) return;

    let cancelled = false;

    me().then(state => {
      const wanted = state.theme?.trim();
      if (cancelled || !wanted || localStorage.getItem(THEME_KEY)) return;

      if (THEMES.some(theme => theme.id === wanted)) setThemeIdState(wanted);
      else console.warn(`WDS_THEME is "${wanted}", which is not one of this studio's themes`);
    }).catch(() => {});

    return () => { cancelled = true; };
  }, []);

  // The command palette cycles themes without knowing the list; this is the one place that does.
  useEffect(() => {
    const cycle = () => setThemeIdState(id => {
      const index = THEMES.findIndex(t => t.id === id);
      const next = THEMES[(index + 1) % THEMES.length].id;
      localStorage.setItem(THEME_KEY, next);
      return next;
    });

    return onShell("cycle-theme", cycle);
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
