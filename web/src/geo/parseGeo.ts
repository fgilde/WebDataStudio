/// Reading geography out of a result. Nothing here talks to the database: whatever the engine
/// handed over — GeoJSON, WKT, or a pair of latitude and longitude columns — is turned into the one
/// shape the map draws.

export type Position = [number, number];

export interface Geometry {
  kind: "point" | "line" | "polygon";
  /// One ring or path per shape. A point has a single position in a single ring.
  rings: Position[][];
}

export interface Feature {
  /// Index into the result's rows, so a shape can say which row it came from.
  row: number;
  geometry: Geometry;
}

const isNumber = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value);

/// Longitude, then latitude — the order GeoJSON and WKT both use, and the order that trips
/// everybody up at least once.
const position = (pair: unknown): Position | null =>
  Array.isArray(pair) && isNumber(pair[0]) && isNumber(pair[1]) ? [pair[0], pair[1]] : null;

function fromGeoJson(value: unknown): Geometry | null {
  if (typeof value !== "object" || value === null) return null;

  const object = value as { type?: unknown; coordinates?: unknown; geometry?: unknown };

  // A Feature wraps the geometry; unwrap once rather than making the caller care.
  if (object.geometry) return fromGeoJson(object.geometry);

  const type = String(object.type ?? "").toLowerCase();
  const coordinates = object.coordinates;

  switch (type) {
    case "point": {
      const point = position(coordinates);
      return point ? { kind: "point", rings: [[point]] } : null;
    }

    case "multipoint": {
      const points = (Array.isArray(coordinates) ? coordinates : [])
        .map(position).filter((p): p is Position => p !== null);
      return points.length > 0 ? { kind: "point", rings: points.map(p => [p]) } : null;
    }

    case "linestring": {
      const line = ring(coordinates);
      return line.length > 1 ? { kind: "line", rings: [line] } : null;
    }

    case "multilinestring": {
      const lines = (Array.isArray(coordinates) ? coordinates : []).map(ring).filter(r => r.length > 1);
      return lines.length > 0 ? { kind: "line", rings: lines } : null;
    }

    case "polygon": {
      const rings = (Array.isArray(coordinates) ? coordinates : []).map(ring).filter(r => r.length > 2);
      return rings.length > 0 ? { kind: "polygon", rings } : null;
    }

    case "multipolygon": {
      const rings = (Array.isArray(coordinates) ? coordinates : [])
        .flatMap(polygon => (Array.isArray(polygon) ? polygon : []).map(ring))
        .filter(r => r.length > 2);
      return rings.length > 0 ? { kind: "polygon", rings } : null;
    }

    default:
      return null;
  }
}

const ring = (value: unknown): Position[] =>
  (Array.isArray(value) ? value : []).map(position).filter((p): p is Position => p !== null);

/// WKT, which is what PostGIS hands over as text and what SQL Server's `.ToString()` produces.
/// SRID prefixes are dropped: the map draws degrees, and a projected coordinate system would need a
/// reprojection this does not pretend to do.
export function fromWkt(text: string): Geometry | null {
  const cleaned = text.trim().replace(/^SRID=\d+;/i, "");
  const match = /^(MULTI)?(POINT|LINESTRING|POLYGON)\s*(Z|M|ZM)?\s*\((.*)\)$/is.exec(cleaned);
  if (!match) return null;

  const multi = Boolean(match[1]);
  const type = match[2].toUpperCase();
  const body = match[4];

  const numbers = (group: string): Position[] =>
    group
      .split(",")
      .map(pair => pair.trim().split(/\s+/).map(Number))
      .filter(parts => parts.length >= 2 && isNumber(parts[0]) && isNumber(parts[1]))
      .map(parts => [parts[0], parts[1]] as Position);

  // The nesting is what the parentheses say: a group per shape, a ring per group.
  const groups = multi || type === "POLYGON"
    ? [...body.matchAll(/\(([^()]*)\)/g)].map(m => m[1])
    : [body];

  const rings = groups.map(numbers).filter(r => r.length > 0);
  if (rings.length === 0) return null;

  if (type === "POINT") return { kind: "point", rings: rings.map(r => [r[0]]) };
  if (type === "LINESTRING") return { kind: "line", rings: rings.filter(r => r.length > 1) };

  return { kind: "polygon", rings: rings.filter(r => r.length > 2) };
}

/// Column names that hold a latitude and a longitude. Checked as whole names rather than as
/// substrings: `template_longitude_note` is not a coordinate.
const LATITUDE = ["lat", "latitude", "y"];
const LONGITUDE = ["lon", "lng", "long", "longitude", "x"];

const normalise = (name: string) => name.toLowerCase().replace(/[^a-z]/g, "");

/// What can be drawn from this result, and how it was recognised. An empty list is the honest
/// answer for a result with no geography in it.
export function featuresOf(
  columns: { name: string; dataType?: string }[], rows: unknown[][],
): { features: Feature[]; source: string | null } {
  // A geometry column first: it is unambiguous, and a table with both should draw the shapes.
  for (const [index, column] of columns.entries()) {
    const features: Feature[] = [];

    for (const [row, values] of rows.entries()) {
      const geometry = parseValue(values[index]);
      if (geometry) features.push({ row, geometry });
    }

    // One value that happens to parse is a coincidence; a column of them is a geometry column.
    if (features.length > 0 && features.length >= Math.min(rows.length, 2))
      return { features, source: column.name };
  }

  const latitude = columns.findIndex(column => LATITUDE.includes(normalise(column.name)));
  const longitude = columns.findIndex(column => LONGITUDE.includes(normalise(column.name)));

  if (latitude >= 0 && longitude >= 0) {
    const features: Feature[] = [];

    for (const [row, values] of rows.entries()) {
      const lat = Number(values[latitude]);
      const lon = Number(values[longitude]);

      // Outside the possible range it is not a coordinate, whatever the column is called.
      if (!isNumber(lat) || !isNumber(lon)) continue;
      if (Math.abs(lat) > 90 || Math.abs(lon) > 180) continue;

      features.push({ row, geometry: { kind: "point", rings: [[[lon, lat]]] } });
    }

    if (features.length > 0)
      return { features, source: `${columns[latitude].name} / ${columns[longitude].name}` };
  }

  return { features: [], source: null };
}

/// One cell as geography, whatever shape it arrived in.
export function parseValue(value: unknown): Geometry | null {
  if (value === null || value === undefined) return null;

  if (typeof value === "object") return fromGeoJson(value);

  if (typeof value !== "string") return null;

  const text = value.trim();
  if (text.length === 0) return null;

  if (text.startsWith("{")) {
    try { return fromGeoJson(JSON.parse(text)); } catch { return null; }
  }

  return fromWkt(text);
}

/// The box every shape fits in, or null when there is nothing to fit.
export function boundsOf(features: Feature[]): { west: number; south: number; east: number; north: number } | null {
  let west = Infinity;
  let south = Infinity;
  let east = -Infinity;
  let north = -Infinity;

  for (const feature of features)
    for (const shape of feature.geometry.rings)
      for (const [lon, lat] of shape) {
        west = Math.min(west, lon);
        east = Math.max(east, lon);
        south = Math.min(south, lat);
        north = Math.max(north, lat);
      }

  if (!Number.isFinite(west)) return null;

  // A single point has no extent; give it one so it lands in the middle rather than dividing by
  // zero on the way there.
  if (east - west < 1e-9) { west -= 0.01; east += 0.01; }
  if (north - south < 1e-9) { south -= 0.01; north += 0.01; }

  return { west, south, east, north };
}
