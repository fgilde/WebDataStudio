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

const createConnection = vi.fn();
const testConnection = vi.fn();

vi.mock("../api", () => ({
  createConnection: (...args: unknown[]) => createConnection(...args),
  testConnection: (...args: unknown[]) => testConnection(...args),
}));

const { StorageWizard } = await import("./StorageWizard");

const draw = (onCreated?: () => void) => render(
  <MantineProvider>
    <StorageWizard opened onClose={() => {}} onCreated={onCreated} />
  </MantineProvider>,
);

const fill = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(label), { target: { value } });

describe("StorageWizard", () => {
  beforeEach(() => {
    cleanup();
    createConnection.mockReset();
    testConnection.mockReset();
  });

  it("says what is still missing instead of a dead Add button with no reason", () => {
    draw();

    expect(screen.getByText("a bucket is needed")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add" })).toHaveProperty("disabled", true);
  });

  it("shows the URL it will store once it can build one", () => {
    draw();

    fill("Bucket", "data-lake");
    fill("Prefix", "exports/2026");
    fill("Region", "eu-central-1");

    expect(screen.getByText("s3://data-lake/exports/2026?region=eu-central-1")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add" })).toHaveProperty("disabled", false);
  });

  it("masks the secret in the preview", () => {
    draw();

    fill("Bucket", "lake");
    fireEvent.click(screen.getByText("Access key and secret"));
    fill("Access key", "AKIA");
    fill("Secret key", "s3cr3t");

    expect(screen.getByText("s3://lake?access=AKIA&secret=…")).toBeTruthy();
    expect(screen.queryByText(/s3cr3t/)).toBeNull();
  });

  it("asks Azure for an account and for the option Azure actually has", () => {
    draw();

    fireEvent.click(screen.getByText("Azure Blob"));

    expect(screen.getByLabelText("Storage account")).toBeTruthy();
    expect(screen.getByLabelText("Container")).toBeTruthy();

    fill("Storage account", "acct");
    fill("Container", "exports");
    fireEvent.click(screen.getByText("Managed identity"));

    expect(screen.getByText("azblob://acct/exports")).toBeTruthy();
  });

  it("switches the sign-in choice when the provider changes rather than carrying a wrong one over", () => {
    draw();

    fill("Bucket", "lake");
    fireEvent.click(screen.getByText("Access key and secret"));
    expect(screen.getByLabelText("Access key")).toBeTruthy();

    fireEvent.click(screen.getByText("Google Cloud"));

    // An S3 access key on a Google bucket would be nonsense, so the choice resets to the default.
    expect(screen.queryByLabelText("Access key")).toBeNull();
    expect(screen.getByLabelText("HMAC key")).toBeTruthy();
  });

  it("needs nothing to authenticate for a folder", () => {
    draw();

    fireEvent.click(screen.getByText("Folder"));
    fill("Path", "/data/incoming");

    expect(screen.getByText("file:///data/incoming")).toBeTruthy();
    expect(screen.queryByLabelText("Sign in with")).toBeNull();
  });

  it("reaches the bucket before anything is saved, and says what it found", async () => {
    testConnection.mockResolvedValue({ ok: true, message: "reached lake: 3 object(s), 1 folder(s)" });

    draw();
    fill("Bucket", "lake");
    fireEvent.click(screen.getByRole("button", { name: "Test" }));

    await waitFor(() => expect(screen.getByText("reached lake: 3 object(s), 1 folder(s)")).toBeTruthy());
    expect(testConnection).toHaveBeenCalledWith(expect.objectContaining({
      engine: "storage", connectionString: "s3://lake", name: "LAKE",
    }));
    expect(createConnection).not.toHaveBeenCalled();
  });

  it("says so when the bucket is not there rather than a green tick", async () => {
    testConnection.mockResolvedValue({ ok: false, message: "The specified bucket does not exist" });

    draw();
    fill("Bucket", "nope");
    fireEvent.click(screen.getByRole("button", { name: "Test" }));

    await waitFor(() => expect(screen.getByText("not reached")).toBeTruthy());
  });

  it("saves the connection with the name, the colour and read-only", async () => {
    createConnection.mockResolvedValue({ id: "c1" });
    const onCreated = vi.fn();

    draw(onCreated);
    fill("Bucket", "lake");
    fill("Name in the studio", "PROD_LAKE");
    fireEvent.click(screen.getByText("Read-only"));
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(createConnection).toHaveBeenCalledWith({
      name: "PROD_LAKE", engine: "storage", connectionString: "s3://lake",
      readOnly: true, color: null,
    }));
    await waitFor(() => expect(onCreated).toHaveBeenCalled());
  });

  it("proposes a name from the bucket so nobody has to invent one", async () => {
    createConnection.mockResolvedValue({ id: "c1" });

    draw();
    fill("Bucket", "data-lake");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(createConnection)
      .toHaveBeenCalledWith(expect.objectContaining({ name: "DATA_LAKE" })));
  });

  it("shows what went wrong when saving fails", async () => {
    createConnection.mockRejectedValue(new Error("a connection named LAKE already exists"));

    draw();
    fill("Bucket", "lake");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(screen.getByText(/already exists/)).toBeTruthy());
  });
});
