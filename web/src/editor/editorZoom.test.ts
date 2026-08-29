// @vitest-environment jsdom
import { describe, expect, it, beforeEach } from "vitest";
import {
  DEFAULT_FONT_SIZE, MAX_FONT_SIZE, MIN_FONT_SIZE,
  clampFontSize, readFontSize, writeFontSize, zoomFor,
} from "./editorZoom";

const wheel = (deltaY: number, ctrlKey = true) => ({ ctrlKey, metaKey: false, deltaY });
const key = (k: string, ctrlKey = true) => ({ ctrlKey, metaKey: false, key: k });

beforeEach(() => localStorage.clear());

describe("zoomFor", () => {
  it("grows on wheel up and shrinks on wheel down, but only with the modifier", () => {
    expect(zoomFor(wheel(-100), 13)).toBe(14);
    expect(zoomFor(wheel(100), 13)).toBe(12);
    expect(zoomFor(wheel(-100, false), 13)).toBeNull();
  });

  it("takes the keys a keyboard actually produces", () => {
    for (const k of ["+", "=", "Add"]) expect(zoomFor(key(k), 13)).toBe(14);
    for (const k of ["-", "_", "Subtract"]) expect(zoomFor(key(k), 13)).toBe(12);
  });

  it("goes back to the default on zero, the way a browser does", () => {
    expect(zoomFor(key("0"), 25)).toBe(DEFAULT_FONT_SIZE);
  });

  it("says nothing about anything else, so typing still types", () => {
    expect(zoomFor(key("a"), 13)).toBeNull();
    expect(zoomFor(key("+", false), 13)).toBeNull();
    expect(zoomFor({ ctrlKey: true, metaKey: false }, 13)).toBeNull();
  });

  it("takes the command key too, because that is the same gesture on a Mac", () => {
    expect(zoomFor({ ctrlKey: false, metaKey: true, deltaY: -100 }, 13)).toBe(14);
  });

  it("stops at both ends rather than reaching an unreadable size", () => {
    expect(zoomFor(wheel(-100), MAX_FONT_SIZE)).toBe(MAX_FONT_SIZE);
    expect(zoomFor(wheel(100), MIN_FONT_SIZE)).toBe(MIN_FONT_SIZE);
  });
});

describe("the size that survives a reload", () => {
  it("starts at the default and keeps what was written", () => {
    expect(readFontSize()).toBe(DEFAULT_FONT_SIZE);

    writeFontSize(18);
    expect(readFontSize()).toBe(18);
  });

  it("clamps what it is given rather than trusting it", () => {
    expect(writeFontSize(999)).toBe(MAX_FONT_SIZE);
    expect(writeFontSize(0)).toBe(MIN_FONT_SIZE);
    expect(clampFontSize(13.4)).toBe(13);
  });

  it("ignores nonsense in storage", () => {
    localStorage.setItem("wds.editor.fontSize", "not a number");
    expect(readFontSize()).toBe(DEFAULT_FONT_SIZE);
  });
});
