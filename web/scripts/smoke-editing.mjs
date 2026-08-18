// Browser check for the data editing path: open a table, edit a cell, read the generated script in
// the preview, apply it, and confirm the new value is in the grid.
// Needs a running server with a writable demo connection (see README, BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).click();
await page.getByText("Tables", { exact: true }).click();

const tableNode = page.getByText("people", { exact: true }).first();
await tableNode.waitFor({ timeout: 15000 });
await tableNode.dblclick();

// The data tab shows the editing toolbar and the seeded rows.
await page.getByRole("button", { name: "Save" }).waitFor({ timeout: 15000 });
const cell = page.locator("td").filter({ hasText: /^ada/ }).first();
await cell.waitFor({ timeout: 15000 });

// A unique value per run keeps the check repeatable against the same database.
const fresh = `ada-${Date.now()}`;
await cell.dblclick();
await page.keyboard.press("Control+a");
await page.keyboard.type(fresh);
await page.keyboard.press("Enter");

await page.getByRole("button", { name: "Save" }).click();
await page.getByText("Review changes").waitFor({ timeout: 10000 });
// The script arrives one request after the modal opens, so wait for it rather than reading now.
await page.getByText(/UPDATE/).first().waitFor({ timeout: 10000 });

await page.getByRole("button", { name: "Apply" }).click();
await page.getByText(fresh, { exact: true }).first().waitFor({ timeout: 15000 });

await page.screenshot({ path: "smoke-editing.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("editing smoke ok");
