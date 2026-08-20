// Browser check for the data tab's column menu — the one opened by double-clicking a table.
// It had the defect the result grid was already fixed for: the filter input sat inside a
// Mantine Menu.Item, which is a button, so it never took the focus and the menu swallowed every
// keystroke as navigation. Hiding a column was not offered at all.
// Needs a running server with the demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).click();
await page.getByText("Tables", { exact: true }).click();

const explorer = page.locator(".dv-groupview").filter({ has: page.getByPlaceholder("Search tables and views") }).first();
const people = explorer.getByText("people", { exact: true }).first();
await people.waitFor({ timeout: 20000 });
await people.dblclick();
await page.getByText("london", { exact: true }).first().waitFor({ timeout: 20000 });

// Scoped to the data panel: the structure panel has a "Name" header of its own, and Playwright's
// hasText is a case-insensitive substring match.
const dataPanel = page.locator(".dv-groupview").filter({ has: page.getByLabel("Reload data") }).first();
const header = name => dataPanel.locator("thead th").filter({ hasText: name }).first();
const headerCount = name => dataPanel.locator("thead th").filter({ hasText: name }).count();
const menu = () => page.locator(".mantine-Menu-dropdown:visible");

// --- the filter has to take the keystrokes ------------------------------------------------------
await header("city").click();
await page.waitForTimeout(500);
const filter = menu().locator("input").first();
await filter.click();
await page.keyboard.type("lon", { delay: 40 });

check("the filter keeps the keystrokes", await filter.inputValue() === "lon");
check("and the focus", await page.evaluate(() =>
  document.activeElement?.tagName === "INPUT"));

await page.keyboard.press("Escape");
// The data tab filters on the server and debounces, so give the round trip a moment.
await page.waitForTimeout(1200);

const body = await dataPanel.locator("tbody").first().innerText();
check("the rows narrow to the match", body.includes("london") && !body.includes("helsinki"));

// Clear it again for the next step.
await header("city").click();
await page.waitForTimeout(400);
await menu().locator("input").first().fill("");
await page.keyboard.press("Escape");
await page.waitForTimeout(1000);

// --- hiding a column and getting it back --------------------------------------------------------
await header("name").click();
await page.waitForTimeout(400);
await menu().getByText("Hide column").click();
await page.keyboard.press("Escape");
await page.waitForTimeout(400);

check("the column is gone", await headerCount("name") === 0);
const indicator = page.getByLabel(/hidden columns/);
check("the indicator counts it", (await indicator.innerText()) === "1");

await indicator.click();
await page.waitForTimeout(300);
await menu().getByText("name", { exact: true }).click();
await page.waitForTimeout(400);

check("and brings it back", await headerCount("name") === 1);
check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await browser.close();
console.log("data menu smoke passed");
