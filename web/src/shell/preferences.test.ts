import { describe, expect, it } from "vitest";
import { comboOf, DEFAULT_PREFERENCES, layer } from "./preferences";

// comboOf reads five fields; a real KeyboardEvent needs a DOM this test does not otherwise want.
const press = (init: Partial<KeyboardEvent>) => comboOf(init as KeyboardEvent);

describe("comboOf", () => {
  it("spells modifiers in a fixed order so two recordings compare equal", () => {
    expect(press({ key: "k", ctrlKey: true, shiftKey: true, altKey: true }))
      .toBe("Ctrl+Alt+Shift+K");
    expect(press({ key: "K", shiftKey: true, ctrlKey: true, altKey: true }))
      .toBe("Ctrl+Alt+Shift+K");
  });

  it("treats the command key like control, because the bindings are written that way", () => {
    expect(press({ key: "e", metaKey: true })).toBe("Ctrl+E");
  });

  it("keeps named keys as they are", () => {
    expect(press({ key: "F5" })).toBe("F5");
    expect(press({ key: "Escape" })).toBe("Escape");
  });

  it("is empty while only a modifier is held: half a binding is not a binding", () => {
    expect(press({ key: "Control", ctrlKey: true })).toBe("");
    expect(press({ key: "Shift", shiftKey: true })).toBe("");
  });
});

describe("layer", () => {
  it("takes what the other side actually says", () => {
    const merged = layer(DEFAULT_PREFERENCES, { pageSize: 500, timeZone: "utc" });

    expect(merged.pageSize).toBe(500);
    expect(merged.timeZone).toBe("utc");
    expect(merged.inspectBeforeRun).toBe(DEFAULT_PREFERENCES.inspectBeforeRun);
  });

  it("ignores a null, because absent has to mean absent", () => {
    // This is the shape a deployment sends when it sets one preference and leaves the rest alone:
    // the file it comes from is allowed to be partial, so every other field arrives as null.
    // Spreading that over the defaults made pageSize null, and the data tab then asked for
    // "limit=" — a 400 for every table in the studio.
    const shipped = {
      pageSize: null, historySnapshots: null, snapshotRows: null,
      inspectBeforeRun: null, notifyAfterSeconds: null, timeZone: "utc",
    } as unknown as Partial<typeof DEFAULT_PREFERENCES>;

    const merged = layer(DEFAULT_PREFERENCES, shipped);

    expect(merged.pageSize).toBe(DEFAULT_PREFERENCES.pageSize);
    expect(merged.snapshotRows).toBe(DEFAULT_PREFERENCES.snapshotRows);
    expect(merged.inspectBeforeRun).toBe(DEFAULT_PREFERENCES.inspectBeforeRun);
    expect(merged.notifyAfterSeconds).toBe(DEFAULT_PREFERENCES.notifyAfterSeconds);
    expect(merged.timeZone).toBe("utc");
  });

  it("keeps a false and a zero, which are answers rather than absences", () => {
    const merged = layer(DEFAULT_PREFERENCES, { inspectBeforeRun: false, notifyAfterSeconds: 0 });

    expect(merged.inspectBeforeRun).toBe(false);
    expect(merged.notifyAfterSeconds).toBe(0);
  });

  it("survives nothing at all", () => {
    expect(layer(DEFAULT_PREFERENCES, null)).toEqual(DEFAULT_PREFERENCES);
    expect(layer(DEFAULT_PREFERENCES, undefined)).toEqual(DEFAULT_PREFERENCES);
    expect(layer(DEFAULT_PREFERENCES, {})).toEqual(DEFAULT_PREFERENCES);
  });
});
