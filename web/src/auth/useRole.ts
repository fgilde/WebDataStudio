import { useEffect, useState } from "react";
import { me } from "../api";

/// The signed-in account's role, or null on a studio without accounts (where everything is
/// allowed). Cached for the lifetime of the page: a role changes with a rollout, not with a click.
let cached: string | null | undefined;

export function useRole(): string | null {
  const [role, setRole] = useState<string | null>(cached ?? null);

  useEffect(() => {
    if (cached !== undefined) return;
    let cancelled = false;
    me().then(state => {
      cached = state.role ?? null;
      if (!cancelled) setRole(cached);
    }).catch(() => { cached = null; });
    return () => { cancelled = true; };
  }, []);

  return role;
}

/// True unless the account is explicitly not an admin. The server refuses either way; this only
/// keeps the UI from offering what it would refuse.
export const isAdmin = (role: string | null) => role === null || role === "admin";
