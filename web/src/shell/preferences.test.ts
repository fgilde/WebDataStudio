import { describe, expect, it } from "vitest";
import { comboOf } from "./preferences";

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
