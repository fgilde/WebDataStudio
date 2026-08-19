import { describe, expect, it } from "vitest";
import { actionsFor, connectionActions, type ExplorerAction } from "./contextActions";

const ids = (kind: string, caps = {}) => actionsFor(kind, caps).map(item => item.action);

describe("actionsFor", () => {
  it("offers a table everything a table can do", () => {
    const table = ids("Table");

    for (const action of ["open-data", "design", "manage-indexes", "export", "script-drop"])
      expect(table).toContain(action as ExplorerAction);
  });

  it("does not offer a view the actions of a table", () => {
    const view = ids("View");

    expect(view).toContain("open-data");
    expect(view).not.toContain("script-truncate");
    expect(view).not.toContain("design");
  });

  it("gives a materialized view its refresh", () => {
    expect(ids("MaterializedView")).toContain("script-refresh-matview");
    expect(ids("View")).not.toContain("script-refresh-matview");
  });

  it("offers a column column-level actions only", () => {
    const column = ids("Column");

    expect(column).toContain("add-index");
    expect(column).toContain("script-drop-column");
    expect(column).not.toContain("open-data");
  });

  it("offers an index its own actions", () => {
    expect(ids("Index")).toEqual(
      ["manage-indexes", "copy-name", "script-reindex", "script-drop-index"]);
  });

  it("offers a foreign key the designer and a drop", () => {
    expect(ids("ForeignKey")).toContain("script-drop-constraint");
  });

  it("lets a container create a table", () => {
    expect(ids("TableFolder")).toContain("new-table");
    expect(ids("Schema")).toContain("new-table");
  });

  it("keeps folders that only list things down to a refresh", () => {
    expect(ids("ViewFolder")).toEqual(["refresh"]);
    expect(ids("SequenceFolder")).toEqual(["refresh"]);
  });

  it("hides everything that writes on an engine without DDL", () => {
    const readOnlyEngine = ids("Table", { ddl: false });

    expect(readOnlyEngine).toContain("open-data");
    expect(readOnlyEngine).not.toContain("design");
    expect(readOnlyEngine).not.toContain("script-drop");
  });

  it("offers databases only where the engine has more than one", () => {
    expect(ids("Database", { multiDatabase: true })).toContain("new-database");
    expect(ids("Database")).not.toContain("new-database");
  });

  it("marks the destructive items", () => {
    const drop = actionsFor("Table").find(item => item.action === "script-drop");
    const open = actionsFor("Table").find(item => item.action === "open-data");

    expect(drop?.danger).toBe(true);
    expect(open?.danger).toBeUndefined();
  });

  it("gives an unknown kind something rather than an empty menu", () => {
    expect(ids("SomethingNew")).toEqual(["refresh"]);
  });

  it("gives a connection row its own short menu", () => {
    expect(connectionActions().map(item => item.action))
      .toEqual(["new-query", "refresh", "properties"]);
    expect(connectionActions({ multiDatabase: true }).map(item => item.action))
      .toContain("new-database");
  });

  it("offers properties where there is a connection behind the node", () => {
    for (const kind of ["Database", "Schema"]) expect(ids(kind)).toContain("properties");

    // A column has no connection string of its own to show.
    expect(ids("Column")).not.toContain("properties");
    expect(ids("Index")).not.toContain("properties");
  });

  it("keeps properties available on an engine without DDL", () => {
    // Reading what a connection is has nothing to do with writing to it.
    expect(ids("Database", { ddl: false })).toContain("properties");
  });

  it("never lists the same action twice in one menu", () => {
    for (const kind of ["Table", "View", "MaterializedView", "Column", "Index", "Schema"]) {
      const actions = ids(kind);
      expect(new Set(actions).size).toBe(actions.length);
    }
  });
});
