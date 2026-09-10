import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { Center, Loader } from "@mantine/core";
import { me, setOnUnauthorized, type Me } from "../api";
import { LoginPage } from "./LoginPage";

export function AuthGate({ children }: { children: ReactNode }) {
  const [state, setState] = useState<Me | null>(null);

  const refresh = useCallback(() => { me().then(setState).catch(() => setState(null)); }, []);

  useEffect(() => {
    setOnUnauthorized(() => setState(s => (s ? { ...s, authenticated: false } : s)));
    refresh();
  }, [refresh]);

  // The name of the studio belongs in the browser tab too, on the login screen already.
  useEffect(() => {
    document.title = state?.title ? `${state.title} · WebDataStudio` : "WebDataStudio";
  }, [state?.title]);

  // And the tab's icon, where a deployment brought its own. The link element is in index.html, so
  // a studio nobody rebranded needs nothing here.
  useEffect(() => {
    if (!state?.icon) return;
    document.querySelector<HTMLLinkElement>("link[rel~='icon']")?.setAttribute("href", state.icon);
  }, [state?.icon]);

  if (!state) return <Center h="100vh"><Loader /></Center>;
  // No credentials configured: no login screen at all.
  if (state.anonymous || state.authenticated) return <>{children}</>;
  return <LoginPage title={state.title} icon={state.icon} sso={state.sso} onSuccess={refresh} />;
}
