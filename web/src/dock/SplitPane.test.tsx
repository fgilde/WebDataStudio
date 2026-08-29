// @vitest-environment jsdom
import { describe, expect, it, beforeEach, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { SplitPane } from "./SplitPane";

/// jsdom gives every element a zero-sized box, and a splitter that divides nothing cannot be
/// dragged. One height for the host is enough to make the arithmetic real.
const withHeight = (height: number) =>
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
    top: 0, left: 0, right: 400, bottom: height, width: 400, height,
    x: 0, y: 0, toJSON: () => ({}),
  } as DOMRect);

const show = (id = "test") => render(
  <MantineProvider>
    <SplitPane id={id} minTop={80} minBottom={80}
      top={<div data-testid="top">sql</div>} bottom={<div data-testid="bottom">rows</div>} />
  </MantineProvider>);

const handle = () => screen.getByRole("separator");
const topPane = () => screen.getByTestId("top").parentElement!;

beforeEach(() => {
  cleanup();
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("SplitPane", () => {
  it("shows both panes with a handle between them", () => {
    show();

    expect(screen.getByTestId("top")).toBeTruthy();
    expect(screen.getByTestId("bottom")).toBeTruthy();
    expect(handle().getAttribute("aria-orientation")).toBe("horizontal");
  });

  it("moves the boundary to where the pointer is", () => {
    withHeight(1000);
    show();

    fireEvent.mouseDown(handle());
    fireEvent.mouseMove(window, { clientY: 700 });
    fireEvent.mouseUp(window);

    expect(topPane().style.height).toBe("70%");
  });

  it("keeps both sides usable however far the drag goes", () => {
    withHeight(1000);
    show();

    fireEvent.mouseDown(handle());
    fireEvent.mouseMove(window, { clientY: 5 });
    expect(topPane().style.height).toBe("8%");

    fireEvent.mouseMove(window, { clientY: 5000 });
    expect(topPane().style.height).toBe("92%");
    fireEvent.mouseUp(window);
  });

  it("stops following the pointer once the button is up", () => {
    withHeight(1000);
    show();

    fireEvent.mouseDown(handle());
    fireEvent.mouseMove(window, { clientY: 300 });
    fireEvent.mouseUp(window);
    fireEvent.mouseMove(window, { clientY: 900 });

    expect(topPane().style.height).toBe("30%");
  });

  it("remembers the ratio for next time, per splitter", () => {
    withHeight(1000);
    show("query");

    fireEvent.mouseDown(handle());
    fireEvent.mouseMove(window, { clientY: 250 });
    fireEvent.mouseUp(window);

    expect(Number(localStorage.getItem("wds.split.query"))).toBeCloseTo(0.25);

    cleanup();
    show("query");
    expect(topPane().style.height).toBe("25%");

    // Another splitter keeps its own answer.
    cleanup();
    show("other");
    expect(topPane().style.height).toBe("50%");
  });

  it("evens it out again on a double click", () => {
    withHeight(1000);
    show();

    fireEvent.mouseDown(handle());
    fireEvent.mouseMove(window, { clientY: 800 });
    fireEvent.mouseUp(window);
    fireEvent.doubleClick(handle());

    expect(topPane().style.height).toBe("50%");
  });

  it("can be moved from the keyboard", () => {
    withHeight(1000);
    show();

    handle().focus();
    fireEvent.keyDown(handle(), { key: "ArrowDown" });

    // Twenty-four pixels of a thousand, give or take what floating point does to it.
    expect(Number.parseFloat(topPane().style.height)).toBeCloseTo(52.4);
  });
});
