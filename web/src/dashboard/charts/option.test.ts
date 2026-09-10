import { describe, expect, it } from "vitest";
import { buildOption, isRefusal, roles, type ChartData } from "./option";
import { emptyWidget, type Widget, type WidgetType } from "../model";
import { OTHER } from "../palette";

const data = (columns: string[], rows: unknown[][]): ChartData => ({
  columns: columns.map(name => ({ name })),
  rows,
});

const widget = (type: WidgetType, patch: Partial<Widget> = {}): Widget => ({
  ...emptyWidget(type),
  ...patch,
});

const byStatus = data(["status", "orders"], [["paid", 12], ["open", 5], ["void", 1]]);

const option = (one: Widget, rows: ChartData = byStatus, dark = false) => {
  const built = buildOption(one, rows, dark);
  if (isRefusal(built)) throw new Error(built.refusal);
  return built.option as Record<string, any>;
};

describe("what a widget draws", () => {
  /// Nobody should have to map columns for `SELECT status, count(*)`.
  it("reads the obvious roles out of the result itself", () => {
    const found = roles(byStatus, {});

    expect(found.category).toBe(0);
    expect(found.values).toEqual([1]);
  });

  it("lets a mapping win over the guess", () => {
    const found = roles(data(["day", "region", "n"], [["mon", "eu", 3]]),
      { category: "region", values: ["n"] });

    expect(found.category).toBe(1);
    expect(found.values).toEqual([2]);
  });

  it("draws a bar per category, on one axis", () => {
    const built = option(widget("Bar"));

    expect(built.series).toHaveLength(1);
    expect(built.series[0].type).toBe("bar");
    expect(built.xAxis.data).toEqual(["paid", "open", "void"]);
    // One value axis. There is no second one to find, here or in the settings.
    expect(Array.isArray(built.yAxis)).toBe(false);
  });

  it("stacks only where the type says so", () => {
    expect(option(widget("Bar")).series[0].stack).toBeUndefined();
    expect(option(widget("StackedBar")).series[0].stack).toBe("total");
    expect(option(widget("StackedArea")).series[0].areaStyle).toBeTruthy();
  });

  /// Identity is never colour alone: two series or more means a legend, and four or fewer are also
  /// labelled directly.
  it("shows a legend from two series and labels up to four", () => {
    const one = option(widget("Bar"));
    expect(one.legend.show).toBe(false);

    const two = option(widget("Bar"), data(["day", "eu", "us"], [["mon", 1, 2], ["tue", 3, 4]]));
    expect(two.legend.show).toBeUndefined();
    expect(two.series).toHaveLength(2);
    expect(two.series[0].label.show).toBe(true);
  });

  it("turns a series column into one series per value", () => {
    const built = option(widget("Line", { mapping: { time: "day", series: "region", values: ["n"] } }),
      data(["day", "region", "n"], [
        ["mon", "eu", 1], ["mon", "us", 2], ["tue", "eu", 3], ["tue", "us", 4],
      ]));

    expect(built.series.map((one: any) => one.name)).toEqual(["eu", "us"]);
    expect(built.series[0].data).toEqual([1, 3]);
  });

  it("folds a ninth series rather than inventing a colour", () => {
    const columns = ["day", ...Array.from({ length: 10 }, (_, i) => `s${i}`)];
    const built = option(widget("Bar"), data(columns, [["mon", ...Array(10).fill(1)]]));

    expect(built.series).toHaveLength(9);
    expect(built.series[8].name).toBe(OTHER);
  });

  it("lays a bar sideways when the labels are long", () => {
    const built = option(widget("Bar"),
      data(["path", "views"], [["/a/very/long/path/that/wraps", 3], ["/b", 1]]));

    expect(built.yAxis.data).toBeTruthy();
    expect(built.xAxis.type).toBe("value");
  });

  it("takes the theme's ink for a dark surface", () => {
    const light = option(widget("Bar"));
    const dark = option(widget("Bar"), byStatus, true);

    expect(light.color[0]).not.toBe(dark.color[0]);
  });

  /// A gauge without a range would need an invented scale, and an invented scale is a lie somebody
  /// reads off a wall.
  it("refuses a gauge with no range, and says why", () => {
    const built = buildOption(widget("Gauge"), data(["n"], [[61]]), false);

    expect(isRefusal(built)).toBe(true);
    if (isRefusal(built)) expect(built.refusal).toContain("smallest");
  });

  it("draws a gauge once it has one", () => {
    const built = option(widget("Gauge", { options: { min: 0, max: 100 } }), data(["n"], [[61]]));

    expect(built.series[0].type).toBe("gauge");
    expect(built.series[0].data[0].value).toBe(61);
  });

  it("gives a pie at most eight slices and one bucket", () => {
    const rows = Array.from({ length: 12 }, (_, i) => [`slice ${i}`, 12 - i]);
    const built = option(widget("Pie"), data(["name", "n"], rows));

    expect(built.series[0].data).toHaveLength(9);
    expect(built.series[0].data[8].name).toBe(OTHER);
  });

  it("needs both dimensions for a heatmap", () => {
    const refused = buildOption(widget("Heatmap"), byStatus, false);
    expect(isRefusal(refused)).toBe(true);

    const built = option(widget("Heatmap", { mapping: { category: "day", series: "hour", values: ["n"] } }),
      data(["day", "hour", "n"], [["mon", "9", 3], ["mon", "10", 5], ["tue", "9", 1]]));

    expect(built.series[0].type).toBe("heatmap");
    // One hue, light to dark — a magnitude is not a set of categories.
    expect(built.visualMap.inRange.color).toHaveLength(6);
  });

  it("needs both ends of a flow", () => {
    const refused = buildOption(widget("Sankey"), byStatus, false);
    expect(isRefusal(refused)).toBe(true);

    const built = option(widget("Sankey", { mapping: { from: "a", to: "b", weight: "n" } }),
      data(["a", "b", "n"], [["eu", "paid", 3], ["us", "paid", 2]]));

    expect(built.series[0].type).toBe("sankey");
    expect(built.series[0].links).toHaveLength(2);
    expect(built.series[0].data.map((one: any) => one.name)).toContain("paid");
  });

  it("says so when there is nothing numeric to draw", () => {
    const built = buildOption(widget("Bar"), data(["a", "b"], [["x", "y"]]), false);

    expect(isRefusal(built)).toBe(true);
  });
});
