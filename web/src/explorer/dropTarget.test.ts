import { describe, expect, it } from "vitest";
import { dragHasFiles, dropKindFor, filesOf } from "./dropTarget";

describe("dropKindFor", () => {
  it("a bucket folder takes the file as it is", () => {
    expect(dropKindFor("Container")).toBe("upload");
    expect(dropKindFor("Prefix")).toBe("upload");
  });

  it("a table takes its rows", () => {
    expect(dropKindFor("Table")).toBe("import");
  });

  it("a schema turns it into a table", () => {
    expect(dropKindFor("Schema")).toBe("new-table");
    expect(dropKindFor("TableFolder")).toBe("new-table");
    expect(dropKindFor("Database")).toBe("new-table");
  });

  it("and everything else takes nothing", () => {
    // A view cannot be written to, an index is not a place for rows, a column is not a table.
    for (const kind of ["View", "Index", "Column", "Function", "Trigger", "StorageObject"])
      expect(dropKindFor(kind)).toBeNull();
  });
});

describe("dragHasFiles", () => {
  it("is true while a file is being dragged, when the names are still hidden", () => {
    expect(dragHasFiles({ items: [{ kind: "file" }], types: ["Files"] } as unknown as DataTransfer))
      .toBe(true);
  });

  it("and false for a selection dragged out of another window", () => {
    expect(dragHasFiles({
      items: [{ kind: "string" }], types: ["text/plain"],
    } as unknown as DataTransfer)).toBe(false);
  });

  it("and false for nothing at all", () => {
    expect(dragHasFiles(null)).toBe(false);
  });
});

describe("filesOf", () => {
  it("reads the files of a drop", () => {
    const file = new File(["a,b\n1,2\n"], "people.csv", { type: "text/csv" });

    expect(filesOf({ files: [file], items: [] } as unknown as DataTransfer)).toEqual([file]);
  });

  it("counts the file items while the drag is still in progress", () => {
    // The browser hides the list until the drop; the count is enough to light a node up.
    const during = filesOf({
      files: [], items: [{ kind: "file" }, { kind: "string" }],
    } as unknown as DataTransfer);

    expect(during).toHaveLength(1);
  });

  it("and answers nothing for a drag with no files", () => {
    expect(filesOf(null)).toEqual([]);
    expect(filesOf({ files: [], items: [{ kind: "string" }] } as unknown as DataTransfer))
      .toEqual([]);
  });
});
