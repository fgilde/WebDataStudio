import { useEffect, useState } from "react";
import { listConnections, listDrivers } from "../api";

/// What the engine behind one connection can do. The UI hides what a driver says it cannot do
/// rather than offering a button that fails.
export type DriverCaps = Record<string, boolean>;

let cached: Promise<Record<string, DriverCaps>> | null = null;

/// Connection id to capabilities. Both lists are small, change only when a connection is added,
/// and are needed by several panels — so they are fetched once per page load.
function load(): Promise<Record<string, DriverCaps>> {
  cached ??= Promise.all([listConnections(), listDrivers()])
    .then(([connections, drivers]) => {
      const byEngine = new Map(drivers.map(driver => [driver.info.id, driver.caps]));
      return Object.fromEntries(connections.map(c => [c.id, byEngine.get(c.engine) ?? {}]));
    })
    .catch(() => ({}));

  return cached;
}

/// Drops the cache — call after adding or editing a connection.
export const forgetDriverCaps = () => { cached = null; };

export function useDriverCaps(connectionId: string | undefined): DriverCaps {
  const [caps, setCaps] = useState<DriverCaps>({});

  useEffect(() => {
    if (!connectionId) { setCaps({}); return; }

    let cancelled = false;
    load().then(all => { if (!cancelled) setCaps(all[connectionId] ?? {}); });
    return () => { cancelled = true; };
  }, [connectionId]);

  return caps;
}
