import { describe, expect, it } from "vitest";
import { panelsToClose, type PanelFacts } from "./closing";

/// The window as it usually looks: the studio's own furniture, then what somebody opened.
const panels: PanelFacts[] = [
  { id: "welcome", pinned: false, layout: true },
  { id: "explorer", pinned: false, layout: true },
  { id: "structure", pinned: false, layout: true },
  { id: "history", pinned: false, layout: true },
  { id: "query-1", pinned: false, layout: false },
  { id: "data-orders", pinned: false, layout: false },
  { id: "query-2", pinned: false, layout: false },
];

describe("panelsToClose", () => {
  it("closes everything opened during the session and nothing the studio arranged", () => {
    expect(panelsToClose("all", panels, "query-1"))
      .toEqual(["query-1", "data-orders", "query-2"]);
  });

  it("keeps the one the menu was opened on when closing the others", () => {
    expect(panelsToClose("others", panels, "query-1")).toEqual(["data-orders", "query-2"]);
  });

  it("closes to the right in the order the tabs are in", () => {
    expect(panelsToClose("right", panels, "data-orders")).toEqual(["query-2"]);
    expect(panelsToClose("right", panels, "query-2")).toEqual([]);
  });

  it("leaves a pinned tab alone, which is what pinning is for", () => {
    const pinned = panels.map(p => (p.id === "data-orders" ? { ...p, pinned: true } : p));

    expect(panelsToClose("all", pinned, "query-1")).toEqual(["query-1", "query-2"]);
    expect(panelsToClose("right", pinned, "query-1")).toEqual(["query-2"]);
  });

  it("closes nothing when there is nothing but furniture", () => {
    expect(panelsToClose("all", panels.filter(p => p.layout), "welcome")).toEqual([]);
  });

  it("says nothing about a tab it cannot find", () => {
    expect(panelsToClose("right", panels, "gone")).toEqual([]);
  });
});
