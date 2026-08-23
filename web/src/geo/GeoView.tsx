import { useMemo, useState } from "react";
import { Badge, Group, Paper, Stack, Text } from "@mantine/core";
import { boundsOf, featuresOf } from "./parseGeo";

/// Geography from a result, drawn. No basemap: a container has no tile server, and a map that
/// silently reaches out to one on the internet is not something a database studio should do on its
/// own. What it does draw is the shapes, to scale, with the coordinates on the edges — enough to
/// see that the points are where they should be, and which one is the outlier.
export function GeoView({ columns, rows }: {
  columns: { name: string; dataType?: string }[];
  rows: unknown[][];
}) {
  const { features, source } = useMemo(() => featuresOf(columns, rows), [columns, rows]);
  const bounds = useMemo(() => boundsOf(features), [features]);
  const [hover, setHover] = useState<number | null>(null);

  if (!bounds || features.length === 0)
    return (
      <Stack gap={4} p="sm">
        <Text size="xs" c="dimmed">
          Nothing geographic in this result. The map reads a GeoJSON or WKT column, or a pair of
          columns called latitude and longitude.
        </Text>
      </Stack>
    );

  const width = 1000;
  // Equirectangular, corrected for the latitude in the middle, so a country does not come out
  // stretched sideways. It is not a projection anybody should measure with; it is a picture.
  const middle = ((bounds.north + bounds.south) / 2) * (Math.PI / 180);
  const span = { x: bounds.east - bounds.west, y: bounds.north - bounds.south };
  const height = Math.max(120, Math.min(900,
    (width * span.y) / Math.max(span.x * Math.cos(middle), 1e-9)));

  const project = ([lon, lat]: [number, number]): [number, number] => [
    ((lon - bounds.west) / span.x) * width,
    height - ((lat - bounds.south) / span.y) * height,
  ];

  const path = (ring: [number, number][], close: boolean) =>
    ring.map((point, index) => {
      const [x, y] = project(point);
      return `${index === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(" ") + (close ? " Z" : "");

  return (
    <Stack gap={4} p={4} h="100%" style={{ minHeight: 0 }}>
      <Group gap="xs">
        <Badge size="xs" variant="light">{features.length} shapes</Badge>
        {source && <Text size="10px" c="dimmed">from {source}</Text>}
        <Text size="10px" c="dimmed">
          {bounds.south.toFixed(3)}…{bounds.north.toFixed(3)} lat,{" "}
          {bounds.west.toFixed(3)}…{bounds.east.toFixed(3)} lon
        </Text>
        {hover !== null && <Badge size="xs" variant="light" color="orange">row {hover + 1}</Badge>}
      </Group>

      <Paper withBorder p={2} style={{ flex: 1, minHeight: 0, overflow: "auto" }}>
        <svg viewBox={`0 0 ${width} ${height}`} width="100%" role="img"
          aria-label="the result's geography" style={{ display: "block" }}>
          {/* A grid, so a shape is read against something rather than floating. */}
          {[0.25, 0.5, 0.75].map(fraction => (
            <g key={fraction}>
              <line x1={width * fraction} x2={width * fraction} y1={0} y2={height}
                stroke="currentColor" strokeOpacity={0.1} />
              <line x1={0} x2={width} y1={height * fraction} y2={height * fraction}
                stroke="currentColor" strokeOpacity={0.1} />
            </g>
          ))}

          {features.map(feature => (
            <g key={feature.row} onMouseEnter={() => setHover(feature.row)}
              onMouseLeave={() => setHover(current => (current === feature.row ? null : current))}
              opacity={hover === null || hover === feature.row ? 1 : 0.35}>
              {feature.geometry.kind === "point"
                ? feature.geometry.rings.map((ring, index) => {
                  const [x, y] = project(ring[0]);
                  return (
                    <circle key={index} cx={x} cy={y} r={hover === feature.row ? 6 : 4}
                      fill="var(--mantine-primary-color-filled)" fillOpacity={0.75}
                      stroke="var(--mantine-color-body)" strokeWidth={1} />
                  );
                })
                : feature.geometry.rings.map((ring, index) => (
                  <path key={index} d={path(ring, feature.geometry.kind === "polygon")}
                    fill={feature.geometry.kind === "polygon"
                      ? "var(--mantine-primary-color-light)"
                      : "none"}
                    stroke="var(--mantine-primary-color-filled)" strokeWidth={1.5}
                    vectorEffect="non-scaling-stroke" />
                ))}
            </g>
          ))}
        </svg>
      </Paper>
    </Stack>
  );
}
