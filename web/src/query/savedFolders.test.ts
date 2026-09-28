import { describe, expect, it } from "vitest";
import { allFolders, moveFolder, normalizeFolder } from "./savedFolders";

describe("saved-query folders", () => {
  it("reads a sloppy path as the folder it means", () => {
    expect(normalizeFolder(" reports// monthly /")).toBe("reports/monthly");
    expect(normalizeFolder("  ")).toBeNull();
  });

  it("lists every folder with its parents, parents first", () => {
    expect(allFolders(["reports/monthly/finance", null, "adhoc", "reports"]))
      .toEqual(["adhoc", "reports", "reports/monthly", "reports/monthly/finance"]);
  });

  it("moves a folder's contents and subfolders, and nothing that only shares the prefix", () => {
    expect(moveFolder("reports/monthly", "reports", "archive/reports")).toBe("archive/reports/monthly");
    expect(moveFolder("reports", "reports", null)).toBeNull();
    expect(moveFolder("reports-old", "reports", "x")).toBe("reports-old");
  });
});
