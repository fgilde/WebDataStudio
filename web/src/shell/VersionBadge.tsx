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
  const label = [
    commit && commit !== "local" ? `commit ${commit.slice(0, 7)}` : "local build",
    Number.isNaN(built.getTime()) ? null : `built ${built.toLocaleString()}`,
  ].filter(Boolean).join(" · ");

  return (
    <Tooltip label={label} position="left" withArrow>
      <Text size="10px" c="dimmed" aria-label="Version" style={{
        position: "fixed", right: 6, bottom: 1, zIndex: 300, pointerEvents: "auto",
      }}>
        v{version}
      </Text>
    </Tooltip>
  );
}
