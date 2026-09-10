import { useEffect, useState } from "react";
import { me, type Me } from "../api";

let cached: Promise<Me | null> | null = null;

/// What the deployment said about itself, fetched once: none of it changes while the page is open.
function useStudio<T>(pick: (state: Me) => T, none: T): T {
  const [value, setValue] = useState<T>(none);

  useEffect(() => {
    cached ??= me().catch(() => null);

    let cancelled = false;
    cached.then(state => { if (!cancelled && state) setValue(pick(state)); });
    return () => { cancelled = true; };
    // The picker is a literal at every call site; re-running on its identity would refetch forever.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return value;
}

/// The studio's name, from WDS_TITLE.
export const useStudioTitle = (): string | null =>
  useStudio(state => state.title ?? null, null);

/// The icon this deployment wants, from WDS_ICON. Null means the one we ship.
export const useStudioIcon = (): string | null =>
  useStudio(state => state.icon ?? null, null);
