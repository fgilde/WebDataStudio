// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const profileObject = vi.fn();
const saveQualityRule = vi.fn();
const getMaskPolicy = vi.fn();
const saveMaskPolicy = vi.fn();

vi.mock("../api", () => ({
  profileObject: (...args: unknown[]) => profileObject(...args),
  saveQualityRule: (...args: unknown[]) => saveQualityRule(...args),
  getMaskPolicy: (...args: unknown[]) => getMaskPolicy(...args),
  saveMaskPolicy: (...args: unknown[]) => saveMaskPolicy(...args),
}));

const { ProfileTab } = await import("./ProfileTab");

const profile = {
  note: null,
  rows: 100,
  columns: [
    {
      name: "id", dataType: "integer", nonNull: 100, nulls: 0, nullPercent: 0, distinct: 100,
      min: "1", max: "100", unique: true, constant: false, masked: false,
    },
    {
      name: "city", dataType: "text", nonNull: 90, nulls: 10, nullPercent: 10, distinct: 12,
      min: "berlin", max: "zurich", unique: false, constant: false, masked: false,
    },
    {
      name: "migrated", dataType: "boolean", nonNull: 100, nulls: 0, nullPercent: 0, distinct: 1,
      min: "true", max: "true", unique: false, constant: true, masked: false,
    },
  ],
  hints: [
    { column: "col_7", looks: "an IBAN", matches: 198, sampled: 200, percent: 99, masked: false },
    { column: "api_key", looks: "a uuid", matches: 200, sampled: 200, percent: 100, masked: true },
  ],
  suggestions: [
    { column: "id", kind: "NotNull", argument: null, why: "every row has a value today" },
    { column: "id", kind: "Unique", argument: null, why: "every value is different today" },
  ],
};

const draw = () => render(
  <MantineProvider>
    <ProfileTab connectionId="c1" objectRef="Table:public/people" table="people" schema="public" />
  </MantineProvider>);

describe("ProfileTab", () => {
  beforeEach(() => {
    cleanup();
    profileObject.mockReset().mockResolvedValue(profile);
    saveQualityRule.mockReset().mockResolvedValue({});
    getMaskPolicy.mockReset().mockResolvedValue({ maskByDefault: true, extra: ["ssn"], never: [] });
    saveMaskPolicy.mockReset().mockResolvedValue(undefined);
  });

  it("counts every column and marks the ones worth noticing", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("100 rows")).toBeTruthy());
    expect(screen.getByText("unique")).toBeTruthy();
    expect(screen.getByText("one value")).toBeTruthy();
    expect(screen.getByText("10%")).toBeTruthy();
  });

  it("says what the values look like, whatever the column is called", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/looks like an IBAN/)).toBeTruthy());
    expect(screen.getByText(/99% of 200 sampled rows/)).toBeTruthy();
    // A column the studio already hides says so instead of offering the same thing again.
    expect(screen.getByText("already masked")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Mask this column" })).toHaveLength(1);
  });

  it("masks a column the values gave away, keeping what was already masked", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/looks like an IBAN/)).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Mask this column" }));

    await waitFor(() => expect(saveMaskPolicy).toHaveBeenCalled());
    expect(saveMaskPolicy.mock.calls[0][1]).toMatchObject({ extra: ["ssn", "col_7"] });
  });

  it("keeps a suggestion as a rule, once", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/always has a value/)).toBeTruthy());

    const [first] = screen.getAllByRole("button", { name: "Keep as a rule" });
    fireEvent.click(first);

    await waitFor(() => expect(saveQualityRule).toHaveBeenCalled());
    expect(saveQualityRule.mock.calls[0][1]).toMatchObject({
      connectionId: "c1", schema: "public", table: "people", column: "id", kind: "NotNull",
    });

    // The button says it is done rather than offering the same rule twice.
    await waitFor(() => expect(screen.getByRole("button", { name: "kept" })).toBeTruthy());
  });

  it("says when a table was too wide to count in one pass", async () => {
    profileObject.mockResolvedValue({ ...profile, note: "the first 60 of 210 columns" });

    draw();

    await waitFor(() => expect(screen.getByText("the first 60 of 210 columns")).toBeTruthy());
  });

  it("shows why a profile could not be read", async () => {
    profileObject.mockRejectedValue(new Error("there is nothing to profile in nope"));

    draw();

    await waitFor(() =>
      expect(screen.getByText("there is nothing to profile in nope")).toBeTruthy());
  });
});
