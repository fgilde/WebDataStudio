// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const objectNotes = vi.fn();
const addNote = vi.fn();
const deleteNote = vi.fn();

vi.mock("../api", () => ({
  objectNotes: (...args: unknown[]) => objectNotes(...args),
  addNote: (...args: unknown[]) => addNote(...args),
  deleteNote: (...args: unknown[]) => deleteNote(...args),
}));

const { NotesTab } = await import("./NotesTab");

const note = (over: Record<string, unknown> = {}) => ({
  id: 1, connectionId: "pg", objectRef: "Table:public/orders", author: "ada",
  body: "The status column is a string because the enum came later.",
  at: "2026-08-28T09:00:00Z", ...over,
});

const draw = () => render(
  <MantineProvider>
    <NotesTab connectionId="pg" objectRef="Table:public/orders" />
  </MantineProvider>);

describe("NotesTab", () => {
  beforeEach(() => {
    cleanup();
    objectNotes.mockReset().mockResolvedValue([]);
    addNote.mockReset();
    deleteNote.mockReset().mockResolvedValue(undefined);
  });

  it("says there is nothing yet rather than showing an empty list", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/Nothing yet/)).toBeTruthy());
    // An empty note is not a note.
    expect(screen.getByRole("button", { name: "Add note" }).hasAttribute("disabled")).toBe(true);
  });

  it("shows who wrote what, and when", async () => {
    objectNotes.mockResolvedValue([note(), note({ id: 2, author: "grace", body: "Second." })]);

    draw();

    await waitFor(() => expect(screen.getByText(/the enum came later/)).toBeTruthy());
    expect(screen.getByText("ada")).toBeTruthy();
    expect(screen.getByText("grace")).toBeTruthy();
  });

  it("adds a note and puts it at the top", async () => {
    addNote.mockResolvedValue(note({ id: 9, body: "Fresh." }));

    draw();

    await waitFor(() => expect(screen.getByText(/Nothing yet/)).toBeTruthy());
    fireEvent.change(screen.getByLabelText("A note about this object"),
      { target: { value: "Fresh." } });
    fireEvent.click(screen.getByRole("button", { name: "Add note" }));

    await waitFor(() => expect(screen.getByText("Fresh.")).toBeTruthy());
    expect(addNote.mock.calls[0]).toEqual(["pg", "Table:public/orders", "Fresh."]);
    // The box is empty again, so the next note does not start with the last one.
    expect((screen.getByLabelText("A note about this object") as HTMLTextAreaElement).value).toBe("");
  });

  it("deletes a note", async () => {
    objectNotes.mockResolvedValue([note()]);

    draw();

    await waitFor(() => expect(screen.getByText(/the enum came later/)).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Delete the note from ada"));

    await waitFor(() => expect(deleteNote).toHaveBeenCalledWith("pg", 1));
    await waitFor(() => expect(screen.queryByText(/the enum came later/)).toBeNull());
  });

  it("shows why a note could not be added", async () => {
    addNote.mockRejectedValue(new Error("this needs the editor role"));

    draw();

    await waitFor(() => expect(screen.getByText(/Nothing yet/)).toBeTruthy());
    fireEvent.change(screen.getByLabelText("A note about this object"),
      { target: { value: "Fresh." } });
    fireEvent.click(screen.getByRole("button", { name: "Add note" }));

    await waitFor(() => expect(screen.getByText("this needs the editor role")).toBeTruthy());
  });
});
