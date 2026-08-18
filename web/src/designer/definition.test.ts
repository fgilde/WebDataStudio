import { describe, expect, it } from "vitest";
import {
  addColumn, emptyDefinition, moveColumn, removeColumn, renameColumn, updateColumn,
} from "./definition";

const base = () => ({
  ...emptyDefinition("public"),
  columns: [
    { name: "id", type: "int", nullable: false, default: null, identity: true, comment: null },
    { name: "name", type: "text", nullable: true, default: null, identity: false, comment: null },
  ],
});

describe("definition", () => {
  it("records where a renamed column came from", () => {
    const renamed = renameColumn(base(), 1, "full_name");
    expect(renamed.columns[1].renamedFrom).toBe("name");
  });

  it("keeps the original origin across a second rename", () => {
    const once = renameColumn(base(), 1, "full_name");
    const twice = renameColumn(once, 1, "label");

    expect(twice.columns[1].name).toBe("label");
    expect(twice.columns[1].renamedFrom).toBe("name");
  });

  it("removing a newly added column leaves no trace", () => {
    const added = addColumn(base());
    const removed = removeColumn(added, added.columns.length - 1);

    expect(removed.columns).toEqual(base().columns);
  });

  it("moves a column without losing its properties", () => {
    const moved = moveColumn(base(), 1, 0);

    expect(moved.columns[0].name).toBe("name");
    expect(moved.columns[0].nullable).toBe(true);
  });

  it("ignores a move past the end", () => {
    const definition = base();
    expect(moveColumn(definition, 0, 5)).toEqual(definition);
  });

  it("patches a single column", () => {
    const patched = updateColumn(base(), 1, { nullable: false, type: "uuid" });

    expect(patched.columns[1].nullable).toBe(false);
    expect(patched.columns[1].type).toBe("uuid");
    expect(patched.columns[0].type).toBe("int");
  });
});
