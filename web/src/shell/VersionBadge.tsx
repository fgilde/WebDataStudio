import { useEffect, useState } from "react";
import { Text, Tooltip } from "@mantine/core";
import { health, type HealthDto } from "../api";

/// The build the studio is actually running, small and out of the way. Without it there is no way
/// to tell a stale container from a fresh one — the reason it exists at all.
export function VersionBadge() {
  const [info, setInfo] = useState<HealthDto | null>(null);

  useEffect(() => { health().then(setInfo).catch(() => setInfo(null)); }, []);
  if (!info) return null;

  const [version, commit] = info.version.split("+");
  const built = new Date(info.built);
  const broken = info.store?.available === false;
  const label = broken
    // The one failure the studio can survive but not hide: nothing can be saved, and the
    // connections in the explorer are only the ones from the environment.
    ? `Storage unavailable — ${(info.store.error ?? info.store.path).replace(/\.$/, "")}. ` +
      "Connections, history and layouts cannot be saved."
    : [
      commit && commit !== "local" ? `commit ${commit.slice(0, 7)}` : "local build",
      Number.isNaN(built.getTime()) ? null : `built ${built.toLocaleString()}`,
    ].filter(Boolean).join(" · ");

  return (
    <Tooltip label={label} position="left" withArrow multiline maw={360}>
      <Text size="10px" c={broken ? "red" : "dimmed"} fw={broken ? 700 : undefined}
        aria-label="Version" style={{
          position: "fixed", right: 6, bottom: 1, zIndex: 300, pointerEvents: "auto",
        }}>
        {broken ? `v${version} · storage unavailable` : `v${version}`}
      </Text>
    </Tooltip>
  );
}
