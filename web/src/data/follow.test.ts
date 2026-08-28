import { describe, expect, it } from "vitest";
import { followColumns, newRows, rowKey, suggestFollowColumn } from "./follow";
import type { DataColumnDto } from "../api";

const column = (name: string, dataType: string): DataColumnDto =>
  ({ name, dataType, nullable: true });

describe("followColumns", () => {
  it("offers the columns that can order a tail, temporal first", () => {
    const columns = [
      column("id", "bigint"), column("name", "text"), column("created_at", "timestamptz"),
    ];

    // A tail sorted by "name" would scroll to a random place and call it latest.
    expect(followColumns(columns, ["id"])).toEqual(["created_at", "id"]);
  });

  it("takes an increasing key even without a timestamp", () => {
    expect(followColumns([column("event_id", "bigint"), column("payload", "jsonb")], ["event_id"]))
      .toEqual(["event_id"]);
  });

  it("does not mistake a foreign key for an increasing one", () => {
    // person_id is numeric and named like a key and increases in no particular order.
    expect(followColumns(
      [column("id", "integer"), column("person_id", "integer"), column("total", "numeric")],
      ["id"])).toEqual(["id"]);
  });

  it("offers nothing where nothing can order it", () => {
    expect(followColumns([column("name", "text"), column("city", "varchar")])).toEqual([]);
    expect(suggestFollowColumn([column("name", "text")])).toBeNull();
  });

  it("suggests the first one, which is the timestamp where there is one", () => {
    expect(suggestFollowColumn([column("id", "int"), column("at", "timestamp")], ["id"])).toBe("at");
  });
});

describe("rowKey", () => {
  const columns = [column("id", "int"), column("name", "text")];

  it("uses the key columns where there are any", () => {
    expect(rowKey([1, "ada"], columns, ["id"])).toBe(rowKey([1, "grace"], columns, ["id"]));
  });

  it("and the whole row where there are none", () => {
    // Two rows that differ in nothing are the same row as far as a tail is concerned.
    expect(rowKey([1, "ada"], columns, [])).toBe(rowKey([1, "ada"], columns, []));
    expect(rowKey([1, "ada"], columns, [])).not.toBe(rowKey([1, "grace"], columns, []));
  });
});

describe("newRows", () => {
  const columns = [column("id", "int"), column("name", "text")];

  it("marks nothing on the first look", () => {
    const seen = new Set<string>();

    // Flashing a whole page the moment following is switched on tells nobody anything.
    expect(newRows([[1, "ada"], [2, "grace"]], columns, ["id"], seen)).toEqual(new Set());
    expect(seen.size).toBe(2);
  });

  it("marks the rows that were not there last time", () => {
    const seen = new Set<string>();
    newRows([[2, "grace"]], columns, ["id"], seen);

    const fresh = newRows([[3, "alan"], [2, "grace"]], columns, ["id"], seen);

    expect(fresh).toEqual(new Set([0]));
  });

  it("marks nothing when nothing arrived", () => {
    const seen = new Set<string>();
    newRows([[1, "ada"]], columns, ["id"], seen);

    expect(newRows([[1, "ada"]], columns, ["id"], seen)).toEqual(new Set());
  });

  it("does not grow without bound while it runs all afternoon", () => {
    const seen = new Set<string>();

    for (let batch = 0; batch < 5; batch++)
      newRows(
        Array.from({ length: 100 }, (_, index) => [batch * 100 + index, "x"]),
        columns, ["id"], seen, 250);

    expect(seen.size).toBeLessThanOrEqual(250);
  });
});
