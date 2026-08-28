// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const jsonShape = vi.fn();
vi.mock("../api", () => ({ jsonShape: (...args: unknown[]) => jsonShape(...args) }));

const { JsonColumnDialog } = await import("./JsonColumnDialog");

const shape = {
  sampled: 200, parsed: 180, note: null,
  paths: [
    {
      path: "user.name", types: ["string"], present: 180, example: "ada",
      expression: "\"payload\"::jsonb #>> '{user,name}'",
    },
    {
      path: "n", types: ["number", "string"], present: 120, example: "1",
      expression: "\"payload\"::jsonb #>> '{n}'",
    },
    {
      path: "tags", types: ["array"], present: 90, example: null,
      expression: "\"payload\"::jsonb #>> '{tags}'",
    },
  ],
  flatten: "SELECT \"payload\"::jsonb #>> '{user,name}' AS \"user_name\"\n  FROM \"events\"",
};

const draw = (onFlatten?: (sql: string) => void) => render(
  <MantineProvider>
    <JsonColumnDialog connectionId="c1" objectRef="Table:public/events" column="payload"
      onClose={() => {}} onFlatten={onFlatten} />
  </MantineProvider>,
);

describe("JsonColumnDialog", () => {
  beforeEach(() => {
    cleanup();
    jsonShape.mockReset();
  });

  it("shows the paths, how often each is there, and how much was sampled", async () => {
    jsonShape.mockResolvedValue(shape);

    draw();

    await waitFor(() => expect(screen.getByText("user.name")).toBeTruthy());
    expect(screen.getByText(/180 of 200 sampled rows read/)).toBeTruthy();
    expect(screen.getByText("180/180")).toBeTruthy();
    expect(screen.getByText("ada")).toBeTruthy();
  });

  it("marks a path that holds two types, because that is where a flatten breaks", async () => {
    jsonShape.mockResolvedValue(shape);

    draw();

    await waitFor(() => expect(screen.getByText("number")).toBeTruthy());
    // Both types are shown rather than the first one winning.
    expect(screen.getAllByText("string").length).toBeGreaterThan(1);
  });

  it("offers the flatten as a query rather than running it", async () => {
    jsonShape.mockResolvedValue(shape);
    const onFlatten = vi.fn();

    draw(onFlatten);

    await waitFor(() => expect(screen.getByRole("button", { name: "Open as a query" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Open as a query" }));

    expect(onFlatten).toHaveBeenCalledWith(shape.flatten);
  });

  it("copies the SQL for one path", async () => {
    jsonShape.mockResolvedValue(shape);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    draw();

    await waitFor(() => expect(screen.getAllByRole("button", { name: "Copy SQL" })).toHaveLength(3));
    fireEvent.click(screen.getAllByRole("button", { name: "Copy SQL" })[0]);

    await waitFor(() => expect(writeText).toHaveBeenCalledWith(shape.paths[0].expression));
  });

  it("says when nothing in the column could be read", async () => {
    jsonShape.mockResolvedValue({
      sampled: 50, parsed: 0, note: "none of the sampled rows is JSON", paths: [], flatten: "",
    });

    draw();

    await waitFor(() => expect(screen.getByText("none of the sampled rows is JSON")).toBeTruthy());
  });

  it("shows what went wrong instead of an empty dialog", async () => {
    jsonShape.mockRejectedValue(new Error("no column 'payload'"));

    draw();

    await waitFor(() => expect(screen.getByText("no column 'payload'")).toBeTruthy());
  });
});
