// Browser check for the P9 additions: command palette, saved queries, query builder, the chart
// view and the parameter prompt. Needs a running server (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];
const expected = /status of (400|404)/;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-p9-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 600));
  throw error;
};

await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 15000 });
await page.getByText("DEMO", { exact: true }).click();

// --- command palette ---------------------------------------------------------
await page.keyboard.press("Control+k");
try {
  await page.getByPlaceholder("Type a command").waitFor({ timeout: 10000 });
  await page.getByPlaceholder("Type a command").fill("diagram");
  await page.getByText("Open ER diagram").first().waitFor({ timeout: 5000 });
  await page.keyboard.press("Enter");
  await page.locator(".react-flow__node").first().waitFor({ timeout: 20000 });
} catch (e) { await fail("palette", e); }

// --- shortcut help ------------------------------------------------------------
await page.keyboard.press("?");
try {
  await page.getByRole("heading", { name: "Keyboard shortcuts" }).waitFor({ timeout: 10000 });
  await page.getByText("Run statement").first().waitFor({ timeout: 5000 });
  await page.keyboard.press("Escape");
} catch (e) { await fail("shortcuts", e); }

// --- saved queries ------------------------------------------------------------
await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").first().waitFor({ timeout: 15000 });
await page.locator(".monaco-editor").first().click();
await page.keyboard.type("SELECT id, city FROM people ORDER BY id");

const name = `smoke-${Date.now()}`;
await page.getByRole("button", { name: "Saved queries" }).click();
try {
  await page.getByRole("button", { name: "Save current query" }).click();
  await page.getByLabel("Name").fill(name);
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await page.getByText(name).first().waitFor({ timeout: 10000 });
} catch (e) { await fail("saved", e); }

// --- chart view ----------------------------------------------------------------
await page.getByRole("tab", { name: /query/i }).last().click().catch(() => {});
await page.locator(".monaco-editor").first().click();
await page.keyboard.press("F5");
try {
  await page.getByText("london", { exact: true }).first().waitFor({ timeout: 20000 });
  await page.getByText("Chart", { exact: true }).click();
  await page.locator("svg[role='img']").first().waitFor({ timeout: 10000 });
} catch (e) { await fail("chart", e); }

// --- parameters ------------------------------------------------------------------
await page.getByText("Grid", { exact: true }).click();
await page.locator(".monaco-editor").first().click();
await page.keyboard.press("Control+a");
await page.keyboard.type("SELECT * FROM people WHERE city = $wanted");
await page.keyboard.press("F5");
try {
  await page.getByText("Query parameters").waitFor({ timeout: 10000 });
  await page.getByLabel("wanted").fill("london");
  await page.getByLabel("Query parameters").getByRole("button", { name: "Run" }).click();
  await page.getByText("london", { exact: true }).first().waitFor({ timeout: 15000 });
} catch (e) { await fail("parameters", e); }

// --- query builder -----------------------------------------------------------------
await page.getByRole("button", { name: "Query builder" }).click();
try {
  await page.getByPlaceholder("Add a table").waitFor({ timeout: 15000 });
  await page.getByPlaceholder("Add a table").click();
  await page.getByText("people", { exact: true }).last().click();
  await page.getByLabel("name", { exact: true }).first().check({ timeout: 10000 });
  await page.getByText("SELECT", { exact: false }).last().waitFor({ timeout: 10000 });
} catch (e) { await fail("builder", e); }

await page.screenshot({ path: "smoke-p9.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-p9 ok");
