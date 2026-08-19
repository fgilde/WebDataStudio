// Browser check for the result grid's column menu: the filter can be typed into and actually
// filters, and a hidden column can be brought back. Both need a real browser — the filter used to
// crash the grid on the first keystroke, which no unit test saw.
// Needs a running server with the demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

// A restored tab would take the focus, and the typing would land in the wrong editor.
await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 20000 });

check(`version badge reads ${await page.getByLabel("Version").innerText()}`,
  /^v\d+\.\d+\./.test(await page.getByLabel("Version").innerText()));

await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").first().waitFor({ timeout: 20000 });
await page.locator(".monaco-editor").first().click();
await page.keyboard.type("SELECT id, name, city FROM people ORDER BY id");
await page.keyboard.press("F5");
await page.getByText("london", { exact: true }).first().waitFor({ timeout: 20000 });

const header = name => page.locator("thead th").filter({ hasText: name }).first();
const menuInput = page.locator(".mantine-Menu-dropdown input");
const menuItem = text => page.locator(".mantine-Menu-dropdown").getByText(text, { exact: true });

// The filter inside the column menu.
await header("city").click();
await page.waitForTimeout(500);
await menuInput.click();
await page.keyboard.type("lon", { delay: 30 });
check("the filter keeps the focus and the keystrokes", await menuInput.inputValue() === "lon");
await page.keyboard.press("Escape");
await page.waitForTimeout(400);
const body = await page.locator("tbody").first().innerText();
check("the filter is applied", body.includes("london") && !body.includes("helsinki"));

await header("city").click();
await page.waitForTimeout(400);
await menuInput.fill("");
await page.keyboard.press("Escape");
await page.waitForTimeout(300);

// Hidden columns, and the way back to them.
await header("name").click();
await page.waitForTimeout(400);
await menuItem("Hide column").click();
await page.keyboard.press("Escape");
await page.waitForTimeout(300);
check("the indicator counts the hidden column",
  (await page.getByLabel(/hidden columns/).innerText()) === "1");

await page.getByLabel(/hidden columns/).click();
await page.waitForTimeout(400);
await menuItem("name").click();
await page.waitForTimeout(400);
check("the column is back", await header("name").count() === 1
  && await page.getByLabel(/hidden columns/).count() === 0);

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));
await browser.close();
console.log("grid smoke passed");
