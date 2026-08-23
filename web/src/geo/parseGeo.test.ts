import { describe, expect, it } from "vitest";
import { boundsOf, featuresOf, fromWkt, parseValue } from "./parseGeo";

describe("fromWkt", () => {
  it("reads a point, longitude first", () => {
    expect(fromWkt("POINT(13.4 52.5)")).toEqual({ kind: "point", rings: [[[13.4, 52.5]]] });
  });

  it("drops an SRID prefix rather than choking on it", () => {
    expect(fromWkt("SRID=4326;POINT(13.4 52.5)")?.kind).toBe("point");
  });

  it("reads a line and a polygon", () => {
    expect(fromWkt("LINESTRING(0 0, 1 1, 2 0)")).toEqual({
      kind: "line", rings: [[[0, 0], [1, 1], [2, 0]]],
    });

    const polygon = fromWkt("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))");
    expect(polygon?.kind).toBe("polygon");
    expect(polygon?.rings[0]).toHaveLength(5);
  });

  it("reads the multi- forms as several shapes", () => {
    expect(fromWkt("MULTIPOINT((0 0),(1 1))")?.rings).toHaveLength(2);
    expect(fromWkt("MULTIPOLYGON(((0 0,1 0,1 1,0 0)),((3 3,4 3,4 4,3 3)))")?.rings).toHaveLength(2);
  });

  it("copes with the Z and M suffixes by ignoring the extra ordinates", () => {
    expect(fromWkt("POINT Z (13.4 52.5 34)")).toEqual({ kind: "point", rings: [[[13.4, 52.5]]] });
  });

  it("is not fooled by text that only looks like geometry", () => {
    expect(fromWkt("POINTLESS(1 2)")).toBeNull();
    expect(fromWkt("hello")).toBeNull();
  });
});

describe("parseValue", () => {
  it("reads GeoJSON as text and as an object", () => {
    const text = '{"type":"Point","coordinates":[13.4,52.5]}';
    expect(parseValue(text)).toEqual({ kind: "point", rings: [[[13.4, 52.5]]] });
    expect(parseValue(JSON.parse(text))).toEqual({ kind: "point", rings: [[[13.4, 52.5]]] });
  });

  it("unwraps a Feature", () => {
    expect(parseValue({
      type: "Feature", properties: {}, geometry: { type: "Point", coordinates: [1, 2] },
    })?.kind).toBe("point");
  });

  it("says nothing about a value that is not geography", () => {
    expect(parseValue(null)).toBeNull();
    expect(parseValue(42)).toBeNull();
    expect(parseValue("{not json")).toBeNull();
    expect(parseValue({ type: "Circle", coordinates: [1, 2] })).toBeNull();
  });
});

describe("featuresOf", () => {
  it("finds a geometry column", () => {
    const found = featuresOf(
      [{ name: "id" }, { name: "shape" }],
      [[1, "POINT(1 2)"], [2, "POINT(3 4)"]]);

    expect(found.features).toHaveLength(2);
    expect(found.source).toBe("shape");
  });

  it("falls back to a latitude and longitude pair", () => {
    const found = featuresOf(
      [{ name: "city" }, { name: "Latitude" }, { name: "lon" }],
      [["Berlin", 52.5, 13.4], ["Lisbon", 38.7, -9.1]]);

    expect(found.features).toHaveLength(2);
    expect(found.source).toBe("Latitude / lon");
    // Longitude first in the position, which is where everybody goes wrong once.
    expect(found.features[0].geometry.rings[0][0]).toEqual([13.4, 52.5]);
  });

  it("refuses coordinates outside the possible range, whatever the column is called", () => {
    const found = featuresOf(
      [{ name: "lat" }, { name: "lon" }],
      [[52.5, 13.4], [1000, 2000]]);

    expect(found.features).toHaveLength(1);
  });

  it("does not call one accidental value a geometry column", () => {
    const found = featuresOf(
      [{ name: "note" }],
      [["POINT(1 2)"], ["a note"], ["another"], ["and another"]]);

    expect(found.features).toEqual([]);
    expect(found.source).toBeNull();
  });

  it("answers with nothing for a result with no geography in it", () => {
    expect(featuresOf([{ name: "id" }, { name: "name" }], [[1, "ada"]]).features).toEqual([]);
  });
});

describe("boundsOf", () => {
  it("is the box every shape fits in", () => {
    const { features } = featuresOf([{ name: "g" }], [["POINT(0 0)"], ["POINT(10 20)"]]);

    expect(boundsOf(features)).toEqual({ west: 0, south: 0, east: 10, north: 20 });
  });

  it("gives a single point an extent, rather than dividing by zero later", () => {
    const { features } = featuresOf([{ name: "g" }], [["POINT(5 5)"], ["POINT(5 5)"]]);
    const bounds = boundsOf(features)!;

    expect(bounds.east).toBeGreaterThan(bounds.west);
    expect(bounds.north).toBeGreaterThan(bounds.south);
  });

  it("is null when there is nothing to fit", () => {
    expect(boundsOf([])).toBeNull();
  });
});
