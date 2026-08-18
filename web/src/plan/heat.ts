/// Maps a node cost onto a background colour. Cool for cheap, hot for expensive, and safe when the
/// plan carries no costs at all.
export function heatColor(cost: number, maxCost: number): string {
  if (!Number.isFinite(cost) || !Number.isFinite(maxCost) || maxCost <= 0) return COOL;

  const ratio = Math.min(Math.max(cost / maxCost, 0), 1);
  if (ratio < 0.15) return COOL;
  if (ratio < 0.4) return "color-mix(in srgb, var(--mantine-color-yellow-6) 12%, transparent)";
  if (ratio < 0.7) return "color-mix(in srgb, var(--mantine-color-orange-6) 18%, transparent)";
  return "color-mix(in srgb, var(--mantine-color-red-6) 22%, transparent)";
}

const COOL = "color-mix(in srgb, var(--mantine-color-blue-6) 8%, transparent)";

export const heatRatio = (cost: number, maxCost: number): number =>
  !Number.isFinite(cost) || maxCost <= 0 ? 0 : Math.min(Math.max(cost / maxCost, 0), 1);
