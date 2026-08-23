import { describe, expect, it } from "vitest";
import {
  dropColumn, dropConstraint, dropIndex, executeRoutine, rebuildIndex,
  selectColumn,
} from "./objectScripts";

describe("object scripts", () => {
  it("quotes identifiers the way each engine does", () => {
    expect(dropColumn("mysql", "`shop`.`people`", "name")).toContain("`name`");
    expect(dropColumn("sqlserver", "[dbo].[people]", "name")).toContain("[name]");
    expect(dropColumn("postgresql", '"public"."people"', "name")).toContain('"name"');
  });

  it("pages a column preview in the dialect's spelling", () => {
    expect(selectColumn("postgresql", "people", "name")).toContain("LIMIT 100");
    expect(selectColumn("sqlserver", "people", "name")).toContain("TOP 100");
  });

  it("names the table when MySQL drops an index", () => {
    expect(dropIndex("mysql", "`people`", "ix_name")).toBe("DROP INDEX `ix_name` ON `people`;");
    expect(dropIndex("postgresql", '"people"', "ix_name")).toBe('DROP INDEX "ix_name";');
  });

  it("rebuilds an index the way the engine can", () => {
    expect(rebuildIndex("postgresql", "t", "ix")).toContain("REINDEX INDEX");
    expect(rebuildIndex("sqlserver", "[t]", "ix")).toContain("REBUILD");
    expect(rebuildIndex("sqlite", "t", "ix")).toContain("REINDEX");
    // MySQL has no per-index rebuild; the script says so instead of pretending.
    expect(rebuildIndex("mysql", "`t`", "ix")).toContain("OPTIMIZE TABLE");
  });

  it("says so rather than guessing where an engine has no rebuild", () => {
    expect(rebuildIndex("clickhouse", "t", "ix")).toContain("--");
  });

  it("drops a foreign key the way MySQL wants it", () => {
    expect(dropConstraint("mysql", "`t`", "fk_x")).toContain("DROP FOREIGN KEY");
    expect(dropConstraint("postgresql", '"t"', "fk_x")).toContain("DROP CONSTRAINT");
  });

  it("calls a routine in the engine's own syntax", () => {
    expect(executeRoutine("sqlserver", "dbo.p")).toBe("EXEC dbo.p;");
    expect(executeRoutine("postgresql", "public.p")).toBe("CALL public.p();");
    expect(executeRoutine("oracle", "p")).toContain("BEGIN");
  });

  it("refreshes a materialized view only where that exists", () => {
  });
});
