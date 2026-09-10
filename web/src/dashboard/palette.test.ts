import { describe, expect, it } from "vitest";
import {
  LOW_CONTRAST_ON_LIGHT, OTHER, SERIES_DARK, SERIES_LIGHT, foldSeries, seriesCap, seriesColour,
  thresholdColour,
} from "./palette";

/// The colour rules, as rules rather than as taste. Every one of these is something the studio's
/// visualisation guidance settles and a chart must not decide for itself.
describe("the chart palette", () => {
  it("is the validated order, in both modes", () => {
    // Blue, orange, aqua, yellow, magenta, green, violet, red — the order is the
    // colour-vision-deficiency mechanism, not a preference, so it is asserted rather than assumed.
    expect(SERIES_LIGHT).toEqual([
      "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948",
    ]);
    expect(SERIES_DARK).toHaveLength(SERIES_LIGHT.length);
    // The dark set is the same hues stepped for a dark surface, not a second palette.
    expect(SERIES_DARK[0]).not.toEqual(SERIES_LIGHT[0]);
  });

  it("assigns by place and never cycles", () => {
    expect(seriesColour(0)).toBe(SERIES_LIGHT[0]);
    expect(seriesColour(7)).toBe(SERIES_LIGHT[7]);
    // A ninth series is not the first hue again: it is the grey everything past the palette wears.
    expect(seriesColour(8)).not.toBe(SERIES_LIGHT[0]);
    expect(seriesColour(0, true)).toBe(SERIES_DARK[0]);
  });

  it("folds a ninth series into one bucket that says so", () => {
    const names = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];

    expect(foldSeries(names)).toEqual(["a", "b", "c", "d", "e", "f", "g", "h", OTHER]);
    expect(foldSeries(["a", "b"])).toEqual(["a", "b"]);
  });

  /// A form where any two series can end up side by side caps at three: past three the palette
  /// cannot clear the all-pairs floor, and re-stepping a documented palette is not on the table.
  it("caps a form where any two series can meet at three", () => {
    expect(seriesCap(false)).toBe(8);
    expect(seriesCap(true)).toBe(3);
    expect(foldSeries(["a", "b", "c", "d"], true)).toEqual(["a", "b", "c", OTHER]);
  });

  /// Three light slots sit below 3:1 on a light surface. The relief rule applies to them, and the
  /// list is in code so it cannot be forgotten.
  it("knows which light slots need relief", () => {
    expect(LOW_CONTRAST_ON_LIGHT).toEqual([2, 3, 4]);
    expect(LOW_CONTRAST_ON_LIGHT.map(index => SERIES_LIGHT[index]))
      .toEqual(["#1baf7a", "#eda100", "#e87ba4"]);
  });

  it("colours a value by the threshold it passed", () => {
    const thresholds = [
      { value: 100, level: "warning" as const },
      { value: 500, level: "critical" as const },
    ];

    expect(thresholdColour(50, thresholds, "#000")).toBe("#000");
    expect(thresholdColour(200, thresholds, "#000")).toBe("#fab219");
    expect(thresholdColour(900, thresholds, "#000")).toBe("#d03b3b");
    expect(thresholdColour(null, thresholds, "#000")).toBe("#000");
    expect(thresholdColour(900, [], "#000")).toBe("#000");
  });
});
