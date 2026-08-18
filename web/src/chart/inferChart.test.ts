import { describe, expect, it } from "vitest";
import { inferChart } from "./inferChart";

const column = (name: string, dataType: string) => ({ name, dataType });

describe("inferChart", () => {
  it("suggests a bar chart for a text column and a numeric column", () => {
    const suggestion = inferChart(
      [column("city", "text"), column("people", "int")],
      Array.from({ length: 20 }, (_, i) => [`c${i}`, i]));

    expect(suggestion).toEqual({ kind: "bar", labelColumn: 0, valueColumns: [1] });
  });

  it("suggests a line chart for a date column and a numeric column", () => {
    const suggestion = inferChart(
      [column("day", "date"), column("total", "numeric")],
      [["2026-01-01", 5], ["2026-01-02", 8]]);

    expect(suggestion?.kind).toBe("line");
    expect(suggestion?.labelColumn).toBe(0);
  });

  it("suggests nothing for two text columns", () => {
    expect(inferChart([column("a", "text"), column("b", "varchar")], [["x", "y"]])).toBeNull();
  });

  it("suggests a pie chart for a single numeric column with few rows", () => {
    expect(inferChart([column("total", "int")], [[1], [2], [3]])?.kind).toBe("pie");
  });

  it("falls back to a bar chart when a single numeric column has many rows", () => {
    expect(inferChart([column("total", "int")], Array.from({ length: 50 }, (_, i) => [i]))?.kind)
      .toBe("bar");
  });

  it("suggests nothing for an empty result", () => {
    expect(inferChart([], [])).toBeNull();
    expect(inferChart([column("a", "int")], [])).toBeNull();
  });

  it("treats numbers arriving as text as numeric", () => {
    const suggestion = inferChart(
      [column("city", "text"), column("people", "unknown")],
      Array.from({ length: 20 }, (_, i) => [`c${i}`, String(i)]));

    expect(suggestion?.kind).toBe("bar");
  });
});
