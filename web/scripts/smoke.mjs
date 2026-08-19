// Browser smoke test: loads the app, opens a query tab, runs a statement and reads the grid.
// Point BASE_URL at a running server (default http://localhost:5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

await page.goto(baseUrl, { waitUntil: "networkidle" });

// The shell renders and the explorer lists the environment connection.
// The product name is the logo now, so its alt text is what identifies the shell.
await page.getByAltText("WebDataStudio").first().waitFor({ timeout: 15000 });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 15000 });

// Expand the connection, then Tables, then confirm a seeded table shows up.
await page.getByText("DEMO", { exact: true }).click();
await page.getByText("Tables", { exact: true }).waitFor({ timeout: 10000 });
await page.getByText("Tables", { exact: true }).click();
const peopleNode = page.locator("aside, div").filter({ hasText: "EXPLORER" }).getByText("people", { exact: true }).first();
await peopleNode.waitFor({ timeout: 10000 });

// Structure panel fills for the selected table. Its content only renders while its dock tab is
// active, and a restored query tab may hold focus, so activate it explicitly.
// Bring the Structure panel to the front first: its content only mounts while its dock tab is
// active, and a restored query tab may hold focus.
await page.getByRole("tab", { name: "Structure" }).click();
await peopleNode.click();
try {
  await page.getByRole("tab", { name: "Columns" }).waitFor({ timeout: 10000 });
} catch (e) {
  await page.screenshot({ path: "smoke-fail.png" });
  console.error("tabs:", await page.getByRole("tab").allInnerTexts());
  console.error("console errors:", errors.slice(0, 3).join(" | ") || "(none)");
  throw e;
}
await page.getByText("active", { exact: true }).first().waitFor({ timeout: 10000 });

// New query tab, type a statement, run it with F5, read a value out of the grid.
await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").waitFor({ timeout: 15000 });
// Monaco's input is a hidden textarea Playwright refuses to fill; typing into the focused editor works.
await page.locator(".monaco-editor").first().click();
// A value the editing smoke does not rewrite, so run order cannot break this one.
await page.keyboard.type("SELECT id, city FROM people ORDER BY id LIMIT 3");
await page.keyboard.press("F5");
try {
  await page.getByText("london", { exact: true }).first().waitFor({ timeout: 15000 });
} catch (e) {
  await page.screenshot({ path: "smoke-fail.png", fullPage: false });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 400));
  throw e;
}

await page.screenshot({ path: "smoke.png", fullPage: false });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke ok");
