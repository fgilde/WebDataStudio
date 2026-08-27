// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
})) as typeof window.matchMedia;

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const exportTemplates = vi.fn();
const saveExportTemplate = vi.fn();
const deleteExportTemplate = vi.fn();

vi.mock("../api", () => ({
  exportTemplates: (...args: unknown[]) => exportTemplates(...args),
  saveExportTemplate: (...args: unknown[]) => saveExportTemplate(...args),
  deleteExportTemplate: (...args: unknown[]) => deleteExportTemplate(...args),
}));

const { TemplateEditor } = await import("./TemplateEditor");

const jira = {
  id: "jira-table", label: "Jira table", extension: "txt", contentType: "text/plain",
  header: "||{{columns}}||", row: "|{{values}}|", footer: null, separator: "|",
};

const draw = (onSaved?: () => void) => render(
  <MantineProvider><TemplateEditor onClose={() => {}} onSaved={onSaved} /></MantineProvider>,
);

describe("TemplateEditor", () => {
  beforeEach(() => {
    cleanup();
    exportTemplates.mockReset();
    saveExportTemplate.mockReset();
    deleteExportTemplate.mockReset();
  });

  it("lists what is there and says which placeholders exist", async () => {
    exportTemplates.mockResolvedValue({ templates: [jira], error: null });

    draw();

    await waitFor(() => expect(screen.getByText("Jira table")).toBeTruthy());
    expect(screen.getByText("{{col.name}}")).toBeTruthy();
    expect(screen.getByText("{{comma}}")).toBeTruthy();
  });

  it("will not save a template with no id or no row", async () => {
    exportTemplates.mockResolvedValue({ templates: [], error: null });

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: "Save" })).toBeTruthy());
    expect(screen.getByRole("button", { name: "Save" })).toHaveProperty("disabled", true);

    fireEvent.change(screen.getByLabelText("Id"), { target: { value: "mine" } });
    expect(screen.getByRole("button", { name: "Save" })).toHaveProperty("disabled", true);

    fireEvent.change(screen.getByLabelText("Row"), { target: { value: "{{values}}" } });
    expect(screen.getByRole("button", { name: "Save" })).toHaveProperty("disabled", false);
  });

  it("saves and tells the export dialog to re-read its formats", async () => {
    exportTemplates.mockResolvedValue({ templates: [], error: null });
    saveExportTemplate.mockResolvedValue(jira);
    const onSaved = vi.fn();

    draw(onSaved);

    await waitFor(() => expect(screen.getByLabelText("Id")).toBeTruthy());
    fireEvent.change(screen.getByLabelText("Id"), { target: { value: "mine" } });
    fireEvent.change(screen.getByLabelText("Row"), { target: { value: "{{values}}" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saveExportTemplate).toHaveBeenCalledWith(
      expect.objectContaining({ id: "mine", row: "{{values}}" })));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
  });

  it("loads a template into the form when its row is clicked", async () => {
    exportTemplates.mockResolvedValue({ templates: [jira], error: null });

    draw();

    await waitFor(() => expect(screen.getByText("Jira table")).toBeTruthy());
    fireEvent.click(screen.getByText("Jira table"));

    expect(screen.getByLabelText("Id")).toHaveProperty("value", "jira-table");
    expect(screen.getByLabelText("Row")).toHaveProperty("value", "|{{values}}|");
  });

  it("deletes one", async () => {
    exportTemplates.mockResolvedValue({ templates: [jira], error: null });
    deleteExportTemplate.mockResolvedValue(undefined);

    draw();

    await waitFor(() => expect(screen.getByLabelText("Delete Jira table")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Delete Jira table"));

    await waitFor(() => expect(deleteExportTemplate).toHaveBeenCalledWith("jira-table"));
  });

  it("shows a bad mounted template as a warning rather than swallowing it", async () => {
    exportTemplates.mockResolvedValue({
      templates: [], error: "ours.json: unexpected end of input",
    });

    draw();

    await waitFor(() => expect(screen.getByText(/unexpected end of input/)).toBeTruthy());
  });
});
