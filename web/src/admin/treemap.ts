export interface TreemapItem { label: string; bytes: number }
export interface TreemapRect {
  label: string; bytes: number; x: number; y: number; width: number; height: number;
}

/// Squarified treemap: rectangles whose area is the size and whose shape stays close to square, so
/// a hundred tables are readable where a sorted list is a hundred lines to scroll.
///
/// The algorithm is Bruls, Huizing and van Wijk's, kept short: fill a row while the aspect ratio
/// improves, lay it out, recurse into what is left.
export function squarify(items: TreemapItem[], width: number, height: number): TreemapRect[] {
  const positive = items.filter(item => item.bytes > 0).sort((a, b) => b.bytes - a.bytes);
  if (positive.length === 0 || width <= 0 || height <= 0) return [];

  const total = positive.reduce((sum, item) => sum + item.bytes, 0);
  const scale = (width * height) / total;

  const rects: TreemapRect[] = [];
  let x = 0;
  let y = 0;
  let free = { width, height };
  let remaining = positive.map(item => ({ ...item, area: item.bytes * scale }));

  while (remaining.length > 0) {
    const short = Math.min(free.width, free.height);
    const row: typeof remaining = [];
    let rowArea = 0;

    // Take items into the row while the worst aspect ratio in it keeps improving.
    while (remaining.length > 0) {
      const candidate = remaining[0];
      const withNext = worstRatio([...row, candidate], rowArea + candidate.area, short);
      const without = row.length > 0 ? worstRatio(row, rowArea, short) : Number.POSITIVE_INFINITY;

      if (row.length > 0 && withNext > without) break;

      row.push(candidate);
      rowArea += candidate.area;
      remaining = remaining.slice(1);
    }

    const thickness = rowArea / short;
    const horizontal = free.width >= free.height;
    let offset = 0;

    for (const item of row) {
      const length = item.area / thickness;

      rects.push(horizontal
        ? { label: item.label, bytes: item.bytes, x, y: y + offset, width: thickness, height: length }
        : { label: item.label, bytes: item.bytes, x: x + offset, y, width: length, height: thickness });

      offset += length;
    }

    if (horizontal) {
      x += thickness;
      free = { width: free.width - thickness, height: free.height };
    } else {
      y += thickness;
      free = { width: free.width, height: free.height - thickness };
    }

    if (free.width <= 0.5 || free.height <= 0.5) break;
  }

  return rects;
}

/// The worst aspect ratio a row would have. Lower is squarer, which is the whole point.
function worstRatio(row: { area: number }[], rowArea: number, short: number): number {
  if (rowArea <= 0) return Number.POSITIVE_INFINITY;

  const thickness = rowArea / short;
  return Math.max(...row.map(item => {
    const length = item.area / thickness;
    return Math.max(thickness / length, length / thickness);
  }));
}
