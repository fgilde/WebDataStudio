import { useEffect, useState } from "react";
import { me } from "../api";

let cached: Promise<string | null> | null = null;

/// The studio's name, from WDS_TITLE. Fetched once: it cannot change while the page is open.
export function useStudioTitle(): string | null {
  const [title, setTitle] = useState<string | null>(null);

  useEffect(() => {
    cached ??= me().then(state => state.title ?? null).catch(() => null);

    let cancelled = false;
    cached.then(value => { if (!cancelled) setTitle(value); });
    return () => { cancelled = true; };
  }, []);

  return title;
}
