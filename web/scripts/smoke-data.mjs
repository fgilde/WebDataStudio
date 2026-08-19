// Browser check for the data tab and the properties dialog, plus a regression check for a wide
// result: a table with many columns used to render its rows collapsed to nothing.
// Needs a running server with a writable demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];
const expected = /status of (400|404)/;

const browser = await chromium.launch();
// Reading the clipboard back needs the permission granted explicitly in a headless browser.
const context = await browser.newContext({
  viewport: { width: 1500, height: 950 },
  permissions: ["clipboard-read", "clipboard-write"],
});
const page = await context.newPage();
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-data-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 600));
  throw error;
};

const node = (label) =>
  page.locator(".mantine-UnstyledButton-root").filter({ hasText: label }).first();

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 15000 });

// --- properties of the connection ------------------------------------------------
try {
  await page.getByText("DEMO", { exact: true }).click({ button: "right" });
  await page.getByText("Properties…").click();
  await page.getByText(/^Properties of/).waitFor({ timeout: 15000 });

  // The definition and what the server itself reports.
  await page.getByText("Data Source=", { exact: false }).first().waitFor({ timeout: 10000 });
  for (const row of ["Name", "Engine", "Access", "Version"])
    await page.getByText(row, { exact: true }).first().waitFor({ timeout: 5000 });

  await page.getByRole("button", { name: "Copy connection string" }).click();
  await page.locator(".mantine-Modal-close").click();
  await page.getByText(/^Properties of/).waitFor({ state: "hidden", timeout: 10000 });
} catch (e) { await fail("properties", e); }

// --- a wide result renders its cells ------------------------------------------------
// Scoped to the tree: the properties dialog also carries the connection's name in a cell.
await node(/^DEMO$/).click();
await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").first().waitFor({ timeout: 15000 });
await page.locator(".monaco-editor").first().click();
await page.keyboard.type("SELECT * FROM Users");
await page.keyboard.press("F5");

try {
  await page.getByText("user1", { exact: true }).first().waitFor({ timeout: 20000 });

  // The bug this guards: 14 rows in the DOM, every cell collapsed to zero width.
  const cell = page.locator("tbody td").filter({ hasText: "user1@example.com" }).first();
  const box = await cell.boundingBox();
  if (!box || box.width < 40 || box.height < 8)
    throw new Error(`a cell of a wide result renders at ${box?.width ?? 0}×${box?.height ?? 0}`);

  // And the header still lines up with the body it describes.
  const header = page.locator("thead th").filter({ hasText: "Email" }).first();
  const headerBox = await header.boundingBox();
  const emailCell = page.locator("tbody td").filter({ hasText: "user1@example.com" }).first();
  const emailBox = await emailCell.boundingBox();
  if (!headerBox || !emailBox || Math.abs(headerBox.x - emailBox.x) > 2)
    throw new Error(`the Email column drifted: header at ${headerBox?.x}, cell at ${emailBox?.x}`);
} catch (e) { await fail("wide", e); }

// --- the data tab can copy and export -----------------------------------------------
await page.getByText("Tables", { exact: true }).click();
const table = node(/^Users$/);
await table.waitFor({ timeout: 15000 });
await table.dblclick();

try {
  await page.getByRole("button", { name: "Save", exact: true }).waitFor({ timeout: 15000 });

  await page.getByRole("button", { name: "Copy" }).click();
  await page.getByText("This page as CSV").waitFor({ timeout: 5000 });
  await page.getByText("This page as CSV").click();

  const clipboard = await page.evaluate(() => navigator.clipboard.readText());
  if (!clipboard.includes("UserName") || !clipboard.includes("user1"))
    throw new Error(`the clipboard holds ${clipboard.slice(0, 120)}`);

  await page.getByRole("button", { name: "Export" }).click();
  await page.getByText(/format/i).first().waitFor({ timeout: 10000 });
  await page.keyboard.press("Escape");
} catch (e) { await fail("datatab", e); }

// --- sorting and filtering happen on the server, not on the page ----------------------
const firstUserName = () => page.locator("tbody tr").first().locator("td").nth(1).innerText();

try {
  const ascending = (await firstUserName()).trim();

  await page.getByText("UserName", { exact: true }).first().click();
  await page.getByText("Sort descending").click();
  await page.keyboard.press("Escape");
  await page.getByText(/sorted by UserName/).waitFor({ timeout: 10000 });

  const descending = (await firstUserName()).trim();
  if (ascending === descending)
    throw new Error(`sorting changed nothing: still ${descending}`);

  await page.getByText("UserName", { exact: true }).first().click();
  await page.getByPlaceholder("Filter UserName").fill("user1");
  await page.getByText(/filtered on UserName/).waitFor({ timeout: 10000 });
  await page.keyboard.press("Escape");

  // user1 and user10 to user14: the server filtered, so the count is not the page size.
  const counter = (await page.locator("body").innerText()).match(/(\d+) rows of/);
  if (counter?.[1] !== "6") throw new Error(`the filter left ${counter?.[1]} rows`);
} catch (e) { await fail("sort", e); }

await page.screenshot({ path: "smoke-data.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-data ok");
