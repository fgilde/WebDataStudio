import { useEffect, useMemo, useState } from "react";
import { Group, Text, Tooltip } from "@mantine/core";
import { listDatabases, type DatabaseDto } from "../api";
import { formatBytes } from "../redis/format";
import { squarify } from "./treemap";

const WIDTH = 760;
const HEIGHT = 320;

/// Where the disk went, as areas rather than a sorted list. A hundred databases are one glance here
/// and a hundred lines to scroll there.
export function SizeTreemap({ connectionId }: { connectionId: string }) {
  const [databases, setDatabases] = useState<DatabaseDto[] | null>(null);

  useEffect(() => {
    let cancelled = false;

    listDatabases(connectionId)
      .then(list => { if (!cancelled) setDatabases(list); })
      .catch(() => { if (!cancelled) setDatabases([]); });

    return () => { cancelled = true; };
  }, [connectionId]);

  const rects = useMemo(() => squarify(
    (databases ?? [])
      .filter(database => (database.sizeBytes ?? 0) > 0)
      .map(database => ({ label: database.name, bytes: database.sizeBytes ?? 0 })),
    WIDTH, HEIGHT), [databases]);

  if (databases === null) return <Text size="xs" c="dimmed">Reading sizes…</Text>;

  if (rects.length === 0)
    return <Text size="xs" c="dimmed">This server does not report sizes for its databases.</Text>;

  const total = rects.reduce((sum, rect) => sum + rect.bytes, 0);

  return (
    <div>
      <Group gap={6} mb={4}>
        <Text size="xs" fw={600}>Size by database</Text>
        <Text size="10px" c="dimmed">{formatBytes(total)} in {rects.length}</Text>
      </Group>

      <svg width={WIDTH} height={HEIGHT} style={{ maxWidth: "100%" }}>
        {rects.map((rect, index) => (
          <Tooltip key={rect.label} label={`${rect.label} · ${formatBytes(rect.bytes)}`} withinPortal>
            <g>
              <rect x={rect.x + 1} y={rect.y + 1} width={Math.max(0, rect.width - 2)}
                height={Math.max(0, rect.height - 2)} rx={3}
                fill="var(--mantine-primary-color-filled)"
                // The largest is the most opaque: the eye reads area first, shade second.
                opacity={0.85 - Math.min(0.55, index * 0.05)} />

              {/* A label only where it fits; a clipped word is worse than no word. */}
              {rect.width > 70 && rect.height > 26 ? (
                <>
                  <text x={rect.x + 7} y={rect.y + 16} fontSize={11} fill="var(--mantine-color-white)">
                    {rect.label.length > Math.floor(rect.width / 7)
                      ? `${rect.label.slice(0, Math.max(1, Math.floor(rect.width / 7) - 1))}…`
                      : rect.label}
                  </text>
                  <text x={rect.x + 7} y={rect.y + 29} fontSize={10} fill="var(--mantine-color-white)"
                    opacity={0.75}>
                    {formatBytes(rect.bytes)}
                  </text>
                </>
              ) : null}
            </g>
          </Tooltip>
        ))}
      </svg>
    </div>
  );
}
