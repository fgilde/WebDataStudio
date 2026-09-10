/// ECharts options, built from a result and a widget.
///
/// One function per family rather than one per type: the fifteen types are seven shapes with
/// different defaults, and the rules that matter — no dual axis, a legend from two series, direct
/// labels up to four, a fixed colour order that folds rather than cycles — hold in one place here
/// instead of in fifteen.

import type { Widget, WidgetMapping, WidgetOptions, WidgetType } from "../model";
import { OTHER, foldSeries, ink, type ChartInk } from "../palette";

export interface ResultColumn { name: string; dataType?: string | null }

export interface ChartData {
  columns: ResultColumn[];
  rows: unknown[][];
}

/// What a widget cannot draw, said in one sentence. The frame prints it in place of the chart: a
/// gauge with no range is not an empty box, it is a widget somebody has not finished.
export interface ChartRefusal { refusal: string }

export type ChartResult = { option: Record<string, unknown> } | ChartRefusal;

export const isRefusal = (result: ChartResult): result is ChartRefusal => "refusal" in result;

/// How many categories a chart draws before the rest becomes one bucket. A bar chart of nine
/// thousand rows is not a bar chart.
const MAX_CATEGORIES = 200;

const num = (value: unknown): number | null => {
  if (value === null || value === undefined || value === "") return null;
  const parsed = typeof value === "number" ? value : Number(String(value));
  return Number.isFinite(parsed) ? parsed : null;
};

const looksNumeric = (data: ChartData, index: number) =>
  data.rows.length > 0
  && data.rows.slice(0, 20).every(row => row[index] === null || num(row[index]) !== null);

const indexOf = (data: ChartData, name: string | null | undefined) =>
  name ? data.columns.findIndex(column => column.name === name) : -1;

/// The column that names the things, and the columns that measure them. A mapping wins; without
/// one, the first non-numeric column names and every numeric column measures — which is what
/// somebody who wrote `SELECT status, count(*)` meant.
export const roles = (data: ChartData, mapping: WidgetMapping) => {
  const named = indexOf(data, mapping.category ?? mapping.time);
  // Minus one where every column is a number: `SELECT count(*)` names nothing, and pretending its
  // one column is a label would leave the value out of the picture.
  const category = named >= 0
    ? named
    : data.columns.findIndex((_, index) => !looksNumeric(data, index));

  const chosen = (mapping.values ?? []).map(name => indexOf(data, name)).filter(index => index >= 0);
  const values = chosen.length > 0
    ? chosen
    : data.columns.map((_, index) => index).filter(index => index !== category && looksNumeric(data, index));

  return { category, values, series: indexOf(data, mapping.series) };
};

const label = (value: unknown) => (value === null || value === undefined ? "∅" : String(value));

const tooltip = (theme: ChartInk) => ({
  trigger: "axis" as const,
  axisPointer: { type: "cross" as const, label: { backgroundColor: theme.axis } },
  backgroundColor: theme.dark ? "#25262b" : "#ffffff",
  borderColor: theme.grid,
  textStyle: { color: theme.text, fontSize: 11 },
});

const axisStyle = (theme: ChartInk) => ({
  axisLine: { lineStyle: { color: theme.axis } },
  axisTick: { show: false },
  axisLabel: { color: theme.muted, fontSize: 10, hideOverlap: true },
  splitLine: { lineStyle: { color: theme.grid, type: "solid" as const } },
});

/// Bars, lines and areas. One value axis — never two: two measures at different scales are two
/// widgets, and there is no switch here to do otherwise.
function cartesian(type: WidgetType, data: ChartData, mapping: WidgetMapping,
  options: WidgetOptions, theme: ChartInk): ChartResult {
  const { category, values, series: seriesColumn } = roles(data, mapping);

  if (values.length === 0) return { refusal: "nothing in this result is a number to draw" };

  const spark = type === "Sparkline";
  const area = type === "Area" || type === "StackedArea";
  const line = area || type === "Line" || spark;
  const stacked = type === "StackedArea" || type === "StackedBar" || options.stacked === true;

  // A series column turns one value column into one series per distinct value — which is how
  // `SELECT day, region, count(*)` is meant to read.
  const pivoted = seriesColumn >= 0
    ? pivot(data, category, seriesColumn, values[0])
    : plain(data, category, values);

  if (pivoted.categories.length === 0) return { refusal: "this result has no rows to draw" };

  const names = foldSeries(pivoted.series.map(one => one.name));
  const drawn = names.map(name => {
    if (name !== OTHER || pivoted.series.some(one => one.name === OTHER))
      return pivoted.series.find(one => one.name === name)!;

    // Everything past the eighth series in one bucket, summed per category: a ninth hue would be a
    // colour nobody validated.
    const rest = pivoted.series.slice(names.length - 1);
    return {
      name: OTHER,
      data: pivoted.categories.map((_, index) =>
        rest.reduce((total, one) => total + (one.data[index] ?? 0), 0)),
    };
  });

  // Labels that are long read better sideways, and that is a rule rather than a preference.
  const horizontal = options.orientation === "horizontal"
    || (!line && pivoted.categories.some(one => one.length > 14));

  const categoryAxis = {
    type: "category" as const,
    data: pivoted.categories,
    ...axisStyle(theme),
    splitLine: { show: false },
  };

  const valueAxis = {
    type: "value" as const,
    ...axisStyle(theme),
    min: options.min ?? undefined,
    max: options.max ?? undefined,
    axisLabel: {
      color: theme.muted, fontSize: 10,
      formatter: (value: number) => format(value, options),
    },
  };

  return {
    option: {
      animation: false,
      color: theme.series,
      grid: spark
        ? { left: 2, right: 2, top: 4, bottom: 2 }
        : { left: 8, right: 12, top: drawn.length > 1 ? 28 : 10, bottom: 6, containLabel: true },
      tooltip: spark ? { show: false } : tooltip(theme),
      legend: drawn.length > 1 && options.legend !== false
        ? { top: 0, textStyle: { color: theme.text, fontSize: 10 }, icon: "roundRect", itemHeight: 8 }
        : { show: false },
      xAxis: horizontal ? valueAxis : { ...categoryAxis, show: !spark },
      yAxis: horizontal ? categoryAxis : { ...valueAxis, show: !spark },
      series: drawn.map(one => ({
        name: one.name,
        type: line ? "line" : "bar",
        data: one.data,
        stack: stacked ? "total" : undefined,
        smooth: false,
        symbol: spark ? "none" : "circle",
        symbolSize: 7,
        lineStyle: line ? { width: 2 } : undefined,
        areaStyle: area ? { opacity: theme.dark ? 0.28 : 0.18 } : undefined,
        // 4px rounded ends on the data end only, anchored to the baseline.
        itemStyle: line ? undefined : {
          borderRadius: horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0],
          // A 2px surface gap between fills, so adjacent marks read as two.
          borderColor: theme.dark ? "#1a1a19" : "#fcfcfb",
          borderWidth: stacked ? 2 : 0,
        },
        // Up to four series carry their own labels, so identity is never colour alone.
        label: !spark && drawn.length <= 4 && !line && pivoted.categories.length <= 12
          ? { show: true, position: horizontal ? "right" : "top", color: theme.text, fontSize: 10,
            formatter: (p: { value: number }) => format(p.value, options) }
          : { show: false },
        endLabel: line && !spark && drawn.length > 1 && drawn.length <= 4
          ? { show: true, color: theme.text, fontSize: 10, formatter: "{a}" }
          : undefined,
      })),
    },
  };
}

interface Series { name: string; data: (number | null)[] }

function plain(data: ChartData, category: number, values: number[]) {
  const rows = data.rows.slice(0, MAX_CATEGORIES);

  return {
    categories: rows.map((row, index) => (category < 0 ? String(index + 1) : label(row[category]))),
    series: values.map<Series>(index => ({
      name: data.columns[index]?.name ?? `column ${index + 1}`,
      data: rows.map(row => num(row[index])),
    })),
  };
}

/// One series per distinct value of a column, which is the shape a `GROUP BY a, b` returns.
function pivot(data: ChartData, category: number, series: number, value: number) {
  const categories: string[] = [];
  const names: string[] = [];
  const cells = new Map<string, number | null>();

  for (const [index, row] of data.rows.entries()) {
    const one = category < 0 ? String(index + 1) : label(row[category]);
    const name = label(row[series]);

    if (!categories.includes(one)) {
      if (categories.length >= MAX_CATEGORIES) continue;
      categories.push(one);
    }
    if (!names.includes(name)) names.push(name);

    cells.set(`${one} ${name}`, num(row[value]));
  }

  return {
    categories,
    series: names.map<Series>(name => ({
      name,
      data: categories.map(one => cells.get(`${one} ${name}`) ?? null),
    })),
  };
}

function pie(type: WidgetType, data: ChartData, mapping: WidgetMapping, options: WidgetOptions,
  theme: ChartInk): ChartResult {
  const { category, values } = roles(data, mapping);
  if (values.length === 0) return { refusal: "nothing in this result is a number to draw" };

  const slices = data.rows
    .map((row, index) => ({
      name: category < 0 ? String(index + 1) : label(row[category]),
      value: num(row[values[0]]) ?? 0,
    }))
    .sort((a, b) => b.value - a.value);

  // Past eight slices the colour order is out of hues, and a pie of thirty slices was the wrong
  // form anyway: the rest becomes one slice that says so.
  const kept = slices.slice(0, 8);
  const rest = slices.slice(8);
  if (rest.length > 0)
    kept.push({ name: OTHER, value: rest.reduce((total, one) => total + one.value, 0) });

  if (type === "Treemap")
    return {
      option: {
        animation: false,
        color: theme.series,
        tooltip: { ...tooltip(theme), trigger: "item" },
        series: [{
          type: "treemap",
          data: slices.slice(0, 60),
          roam: false,
          nodeClick: false,
          breadcrumb: { show: false },
          label: { show: true, color: "#ffffff", fontSize: 11 },
          itemStyle: { borderColor: theme.dark ? "#1a1a19" : "#fcfcfb", borderWidth: 2, gapWidth: 2 },
        }],
      },
    };

  return {
    option: {
      animation: false,
      color: theme.series,
      tooltip: { ...tooltip(theme), trigger: "item" },
      legend: options.legend !== false
        ? { top: 0, textStyle: { color: theme.text, fontSize: 10 }, icon: "roundRect", itemHeight: 8 }
        : { show: false },
      series: [{
        type: "pie",
        radius: ["45%", "72%"],
        center: ["50%", "56%"],
        data: kept,
        // Direct labels, because a legend alone makes colour the only identity.
        label: { color: theme.text, fontSize: 10, formatter: "{b}: {d}%" },
        labelLine: { lineStyle: { color: theme.axis } },
        itemStyle: { borderColor: theme.dark ? "#1a1a19" : "#fcfcfb", borderWidth: 2 },
      }],
    },
  };
}

function heatmap(data: ChartData, mapping: WidgetMapping, options: WidgetOptions,
  theme: ChartInk): ChartResult {
  const { category, values, series } = roles(data, mapping);

  if (series < 0)
    return { refusal: "a heatmap needs a second dimension: pick the column for its rows in the settings" };
  if (values.length === 0) return { refusal: "nothing in this result is a number to draw" };

  const columns: string[] = [];
  const rows: string[] = [];
  const cells: [number, number, number][] = [];

  for (const [index, row] of data.rows.entries()) {
    const x = category < 0 ? String(index + 1) : label(row[category]);
    const y = label(row[series]);
    if (!columns.includes(x)) columns.push(x);
    if (!rows.includes(y)) rows.push(y);
    cells.push([columns.indexOf(x), rows.indexOf(y), num(row[values[0]]) ?? 0]);
  }

  const magnitudes = cells.map(one => one[2]);

  return {
    option: {
      animation: false,
      tooltip: { ...tooltip(theme), trigger: "item" },
      grid: { left: 8, right: 12, top: 8, bottom: 40, containLabel: true },
      xAxis: { type: "category", data: columns, ...axisStyle(theme), splitLine: { show: false } },
      yAxis: { type: "category", data: rows, ...axisStyle(theme), splitLine: { show: false } },
      // One hue, light to dark: a magnitude is not a set of categories.
      visualMap: {
        min: options.min ?? Math.min(...magnitudes, 0),
        max: options.max ?? Math.max(...magnitudes, 1),
        orient: "horizontal",
        left: "center",
        bottom: 0,
        itemHeight: 60,
        textStyle: { color: theme.muted, fontSize: 10 },
        inRange: { color: theme.sequential },
      },
      series: [{
        type: "heatmap",
        data: cells,
        itemStyle: { borderColor: theme.dark ? "#1a1a19" : "#fcfcfb", borderWidth: 2 },
        emphasis: { itemStyle: { borderColor: theme.text, borderWidth: 2 } },
      }],
    },
  };
}

function sankey(data: ChartData, mapping: WidgetMapping, theme: ChartInk): ChartResult {
  const from = indexOf(data, mapping.from);
  const to = indexOf(data, mapping.to);
  const weight = indexOf(data, mapping.weight);

  if (from < 0 || to < 0)
    return { refusal: "a flow needs a column it comes from and one it goes to — pick both in the settings" };

  const nodes = new Set<string>();
  const links = data.rows.slice(0, 300).map(row => {
    const source = label(row[from]);
    const target = label(row[to]);
    nodes.add(source);
    nodes.add(target);
    return { source, target, value: weight >= 0 ? num(row[weight]) ?? 1 : 1 };
  }).filter(link => link.source !== link.target);

  return {
    option: {
      animation: false,
      color: theme.series,
      tooltip: { ...tooltip(theme), trigger: "item" },
      series: [{
        type: "sankey",
        data: [...nodes].map(name => ({ name })),
        links,
        emphasis: { focus: "adjacency" },
        label: { color: theme.text, fontSize: 10 },
        lineStyle: { color: "gradient", opacity: 0.35 },
      }],
    },
  };
}

function gauge(data: ChartData, mapping: WidgetMapping, options: WidgetOptions,
  theme: ChartInk): ChartResult {
  // A gauge without a range is a gauge whose scale would be invented, and an invented scale is a
  // lie somebody reads off a wall.
  if (options.min === null || options.min === undefined
    || options.max === null || options.max === undefined)
    return { refusal: "a gauge needs a smallest and a largest value — set both in the settings" };

  const { values } = roles(data, mapping);
  const value = data.rows.length > 0 && values.length > 0 ? num(data.rows[0][values[0]]) : null;

  if (value === null) return { refusal: "this result has no number to point at" };

  const span = Math.max(1e-9, options.max - options.min);
  const stops = [...(options.thresholds ?? [])].sort((a, b) => a.value - b.value);

  return {
    option: {
      animation: false,
      series: [{
        type: "gauge",
        min: options.min,
        max: options.max,
        startAngle: 210,
        endAngle: -30,
        radius: "92%",
        center: ["50%", "62%"],
        progress: { show: true, width: 12, roundCap: true },
        pointer: { show: false },
        axisLine: {
          lineStyle: {
            width: 12,
            color: stops.length > 0
              // Their own bands, in the reserved status colours and nothing else.
              ? bands(stops, options.min, span, theme)
              : [[1, theme.grid]],
          },
        },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { color: theme.muted, fontSize: 9, distance: 14 },
        detail: {
          valueAnimation: false,
          color: theme.text,
          fontSize: 22,
          offsetCenter: [0, "-8%"],
          formatter: (one: number) => format(one, options),
        },
        data: [{ value }],
      }],
    },
  };
}

const bands = (
  stops: { value: number; level: string }[],
  min: number,
  span: number,
  theme: ChartInk,
): [number, string][] => {
  const status: Record<string, string> = {
    good: "#0ca30c", warning: "#fab219", serious: "#ec835a", critical: "#d03b3b",
  };

  const list: [number, string][] = [];
  let previous = theme.grid;

  for (const stop of stops) {
    list.push([Math.min(1, Math.max(0, (stop.value - min) / span)), previous]);
    previous = status[stop.level] ?? theme.grid;
  }

  list.push([1, previous]);
  return list;
};

export const format = (value: number | null, options: WidgetOptions): string => {
  if (value === null) return "—";

  const decimals = options.decimals ?? undefined;
  const text = value.toLocaleString(undefined, decimals === undefined
    ? { maximumFractionDigits: 2 }
    : { minimumFractionDigits: decimals, maximumFractionDigits: decimals });

  return options.unit ? `${text} ${unitLabel(options.unit)}` : text;
};

/// Grafana's unit ids are what an imported dashboard carries, and the handful somebody actually
/// uses read better as their symbol.
const UNITS: Record<string, string> = {
  percent: "%", percentunit: "%", short: "", none: "",
  currencyEUR: "€", currencyUSD: "$", currencyGBP: "£",
  bytes: "B", decbytes: "B", ms: "ms", s: "s", seconds: "s", requests: "req",
};

export const unitLabel = (unit: string) => UNITS[unit] ?? unit;

export function buildOption(widget: Widget, data: ChartData, dark: boolean): ChartResult {
  const theme = ink(dark);
  const { type, mapping, options } = widget;

  if (data.columns.length === 0) return { refusal: "this widget's statement returned no columns" };

  switch (type) {
    case "Gauge":
      return gauge(data, mapping, options, theme);
    case "Pie":
    case "Treemap":
      return pie(type, data, mapping, options, theme);
    case "Heatmap":
      return heatmap(data, mapping, options, theme);
    case "Sankey":
      return sankey(data, mapping, theme);
    default:
      return cartesian(type, data, mapping, options, theme);
  }
}
