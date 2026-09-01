// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { Pager, pageCount, rangeLabel } from "./Pager";

// jsdom has no scrollIntoView, and Mantine's dropdown reaches for it on a timer after the click —
// after the test has finished, where it surfaces as an unhandled error rather than a failure.
beforeAll(() => { Element.prototype.scrollIntoView = vi.fn(); });

// Auto-cleanup is not wired in this project, and a left-over render is a second
// "Go to page" box for the next test to trip over.
afterEach(cleanup);

const show = (props: Partial<Parameters<typeof Pager>[0]> = {}) =>
  render(
    <MantineProvider>
      <Pager page={1} pageSize={200} rowsOnPage={200} total={12345}
        onPage={() => {}} onPageSize={() => {}} {...props} />
    </MantineProvider>,
  );

// Grouping follows the machine, the way every other number in the studio does, so the expectations
// are built the same way rather than hard-coded to one locale.
const n = (value: number) => value.toLocaleString();

describe("the range a pager shows", () => {
  it("counts rows rather than pages", () => {
    expect(rangeLabel(1, 200, 200, 12345)).toBe(`1–200 of ${n(12345)}`);
    expect(rangeLabel(3, 200, 200, 12345)).toBe(`401–600 of ${n(12345)}`);
  });

  it("ends where the last page ends", () => {
    // The last page is short, and saying 12,401–12,600 of 12,345 would be nonsense.
    expect(rangeLabel(63, 200, 145, 12345)).toBe(`${n(12401)}–${n(12545)} of ${n(12345)}`);
  });

  it("marks a total the engine only guessed", () => {
    expect(rangeLabel(1, 200, 200, 12345, true)).toBe(`1–200 of ≈${n(12345)}`);
  });

  it("does not pretend a table's total is a filtered result's", () => {
    expect(rangeLabel(1, 200, 200, 12345, true, true)).toBe("1–200 of ?");
    expect(rangeLabel(1, 200, 200, null)).toBe("1–200 of ?");
  });

  it("says so when there is nothing", () => {
    expect(rangeLabel(1, 200, 0, 0)).toBe("no rows");
  });
});

describe("how far a pager can go", () => {
  it("divides the total into pages", () => {
    expect(pageCount(200, 12345)).toBe(62);
    expect(pageCount(200, 200)).toBe(1);
    expect(pageCount(200, null)).toBe(1);
  });
});

describe("the pager", () => {
  it("jumps to a page that was typed", () => {
    const onPage = vi.fn();
    show({ onPage, total: 12345 });

    const box = screen.getByLabelText("Go to page");
    fireEvent.change(box, { target: { value: "42" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(onPage).toHaveBeenCalledWith(42);
  });

  it("does not jump past the end", () => {
    const onPage = vi.fn();
    // Six pages, so the box is offered at all: it appears once there are more than five.
    show({ onPage, total: 1200, pageSize: 200 });

    const box = screen.getByLabelText("Go to page");
    fireEvent.change(box, { target: { value: "900" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(onPage).toHaveBeenCalledWith(6);
  });

  it("changes the page size", async () => {
    const onPageSize = vi.fn();
    show({ onPageSize });

    // The label is on the input and on the list it opens, so the input is asked for by role.
    fireEvent.click(screen.getByRole("combobox", { name: "Rows per page" }));
    fireEvent.click(await screen.findByText("50"));

    await waitFor(() => expect(onPageSize).toHaveBeenCalledWith(50));
  });

  it("offers to count only where the number on show is not one", () => {
    const onCount = vi.fn();

    show({ onCount, totalIsEstimate: false, filtered: false });
    expect(screen.queryByLabelText("Count rows")).toBeNull();
    cleanup();


    show({ onCount, totalIsEstimate: true });
    fireEvent.click(screen.getByLabelText("Count rows"));
    expect(onCount).toHaveBeenCalled();
  });
});
