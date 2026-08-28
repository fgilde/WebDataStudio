import { describe, it, expect } from "vitest";
import { carriesZone, describeZone, formatTimestamp, looksTimestamp } from "./formatTime";

describe("a timestamp on the way to the screen", () => {
  it("recognises what the drivers write", () => {
    expect(looksTimestamp("2026-08-29T14:00:00.0000000Z")).toBe(true);
    expect(looksTimestamp("2026-08-29T14:00:00")).toBe(true);
    expect(looksTimestamp("2026-08-29 14:00:00+02:00")).toBe(true);

    expect(looksTimestamp("2026-08-29")).toBe(false);
    expect(looksTimestamp("hello")).toBe(false);
    expect(looksTimestamp(42)).toBe(false);
  });

  it("is read by a person: no T, no seven decimal places", () => {
    expect(formatTimestamp("2026-08-29T14:00:00.0000000Z", "utc").text).toBe("2026-08-29 14:00:00");
  });

  it("keeps a fraction that says something", () => {
    expect(formatTimestamp("2026-08-29T14:00:00.1230000Z", "utc").text).toBe("2026-08-29 14:00:00.123");
  });

  /// The whole point: 14:00 UTC is 16:00 in Berlin, and somebody deleting "yesterday's" rows needs
  /// to know which of the two they are looking at.
  it("shows a value that knows its zone on the clock that was chosen", () => {
    expect(formatTimestamp("2026-08-29T14:00:00Z", "Europe/Berlin").text).toBe("2026-08-29 16:00:00");
    expect(formatTimestamp("2026-08-29T14:00:00Z", "utc").text).toBe("2026-08-29 14:00:00");
    expect(formatTimestamp("2026-08-29T16:00:00+02:00", "utc").text).toBe("2026-08-29 14:00:00");
  });

  /// A `timestamp without time zone` holding 14:00 means 14:00. Turning it into 16:00 because the
  /// reader sits in Berlin would be an invention, so it is never converted.
  it("never converts a value that carries no zone", () => {
    const shown = formatTimestamp("2026-08-29T14:00:00", "Europe/Berlin");

    expect(shown.text).toBe("2026-08-29 14:00:00");
    expect(shown.zoned).toBe(false);
  });

  it("falls back to the raw value on a zone this browser does not know", () => {
    expect(formatTimestamp("2026-08-29T14:00:00Z", "Mars/Olympus").text)
      .toBe("2026-08-29T14:00:00Z");
  });

  it("leaves anything that is not a timestamp alone", () => {
    expect(formatTimestamp("not a time", "utc").text).toBe("not a time");
  });
});

describe("what the footer says", () => {
  it("says nothing when the clock is the reader's own", () => {
    expect(describeZone("local")).toBeNull();
  });

  it("names the clock otherwise", () => {
    expect(describeZone("utc")).toBe("times in UTC");
    expect(describeZone("Europe/Berlin")).toBe("times in Europe/Berlin");
  });
});

describe("what a column's type says about zones", () => {
  it("knows the ones that keep a zone", () => {
    expect(carriesZone("timestamptz")).toBe(true);
    expect(carriesZone("timestamp with time zone")).toBe(true);
    expect(carriesZone("datetimeoffset")).toBe(true);
  });

  it("and the ones that do not", () => {
    expect(carriesZone("timestamp")).toBe(false);
    expect(carriesZone("datetime2")).toBe(false);
    expect(carriesZone("date")).toBe(false);
  });

  it("says nothing about a column that is not a time at all", () => {
    expect(carriesZone("text")).toBeNull();
    expect(carriesZone("integer")).toBeNull();
  });
});
