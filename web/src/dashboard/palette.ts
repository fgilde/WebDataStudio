/// The colours a dashboard draws with.
///
/// The studio ships twenty-three themes; charts do not get twenty-three palettes. They get one
/// validated categorical order in two stepped sets — one for light surfaces, one for dark — so a
/// widget is written against a slot rather than a hex, and the same series keeps the same colour
/// wherever it appears.
///
/// The order is the eight-hue reference instance from the studio's visualisation guidance, and it
/// is not ours to re-step: it clears the colour-vision-deficiency and normal-vision gates on
/// adjacent pairs in both modes, and those gates were checked by a validator rather than by eye.

import type { ThresholdLevel } from "./model";

/// Slot order: blue, orange, aqua, yellow, magenta, green, violet, red.
export const SERIES_LIGHT = [
  "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948",
];

export const SERIES_DARK = [
  "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767",
];

/// Everything past the eighth series. It is a colour, not a ninth hue: a generated hue would be a
/// colour nobody validated, so what does not fit becomes one bucket that says so.
export const OTHER_LIGHT = "#898781";
export const OTHER_DARK = "#898781";

/// The label that bucket carries. Named here because both the palette and the widgets fold the
/// same way.
export const OTHER = "Other";

/// One hue, light to dark: the encoding for a magnitude (a heatmap, a choropleth). Never a rainbow.
export const SEQUENTIAL_LIGHT = ["#cde2fb", "#9ec5f4", "#6da7ec", "#3987e5", "#256abf", "#184f95"];
export const SEQUENTIAL_DARK = ["#104281", "#184f95", "#256abf", "#3987e5", "#6da7ec", "#9ec5f4"];

/// Two hues and a grey midpoint, for a value with a sign. Never a hue in the middle.
export const DIVERGING_LIGHT = ["#2a78d6", "#f0efec", "#d03b3b"];
export const DIVERGING_DARK = ["#3987e5", "#383835", "#e66767"];

/// Reserved for what a threshold means, and never used as "series four".
export const STATUS: Record<ThresholdLevel, string> = {
  good: "#0ca30c",
  warning: "#fab219",
  serious: "#ec835a",
  critical: "#d03b3b",
};

/// The three light slots that sit below 3:1 on a light surface. The relief rule applies to them:
/// visible direct labels, or the table view. It is not a warning to dismiss, which is why the list
/// is in code rather than in a comment.
export const LOW_CONTRAST_ON_LIGHT = [2, 3, 4];

export interface ChartInk {
  dark: boolean;
  series: string[];
  other: string;
  sequential: string[];
  diverging: string[];
  text: string;
  muted: string;
  grid: string;
  axis: string;
  surface: string;
}

export const ink = (dark: boolean): ChartInk => dark
  ? {
    dark,
    series: SERIES_DARK,
    other: OTHER_DARK,
    sequential: SEQUENTIAL_DARK,
    diverging: DIVERGING_DARK,
    text: "#ffffff",
    muted: "#898781",
    grid: "#2c2c2a",
    axis: "#383835",
    surface: "transparent",
  }
  : {
    dark,
    series: SERIES_LIGHT,
    other: OTHER_LIGHT,
    sequential: SEQUENTIAL_LIGHT,
    diverging: DIVERGING_LIGHT,
    text: "#0b0b0b",
    muted: "#898781",
    grid: "#e1e0d9",
    axis: "#c3c2b7",
    surface: "transparent",
  };

/// The colour for a series by its place in the list — never cycled: everything past the eighth is
/// the "Other" grey, and folding those rows into one series is the widget's job.
export const seriesColour = (index: number, dark = false): string => {
  const set = dark ? SERIES_DARK : SERIES_LIGHT;
  return index < set.length ? set[index] : (dark ? OTHER_DARK : OTHER_LIGHT);
};

/// How many series a form may draw before folding. Eight where series sit side by side in a fixed
/// order — a stack, a bar, a line — and three where any two can end up adjacent, because past
/// three the palette cannot clear the all-pairs floor and re-stepping a documented palette is not
/// on the table.
export const seriesCap = (allPairs: boolean) => (allPairs ? 3 : 8);

/// The series a widget draws, with everything past the cap folded into one. Returns the names in
/// the order they will be drawn, so a legend and a mark cannot disagree.
export const foldSeries = (names: string[], allPairs = false): string[] => {
  const cap = seriesCap(allPairs);
  return names.length <= cap ? names : [...names.slice(0, cap), OTHER];
};

/// A threshold's colour for a value: the highest threshold at or below it, or the theme's own ink
/// when no threshold applies. A state ships with its number as well — see the stat widget, which
/// prints both.
export const thresholdColour = (
  value: number | null,
  thresholds: { value: number; level: ThresholdLevel }[] | undefined,
  fallback: string,
): string => {
  if (value === null || !thresholds?.length) return fallback;

  const passed = [...thresholds].sort((a, b) => a.value - b.value)
    .filter(one => value >= one.value).pop();

  return passed ? STATUS[passed.level] : fallback;
};
