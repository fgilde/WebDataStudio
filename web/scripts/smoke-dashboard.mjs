// Browser check for the dashboard: that it is in the Tools menu at all, and that a tile can be
// filled in while the dashboard is made — both of which were broken on the first cut. Needs a
// running server with a connection that has a `customers` and an `orders` table (the demo seed
// has both). BASE_URL defaults to :5005.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });

page.on("console", m => { if (m.type() === "error" && !/status of 40[04]/.test(m.text())) errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });

const connections = await (await page.request.get(`${baseUrl}/api/connections`)).json();
await page.getByText(connections[0].name, { exact: true }).first().click();

// --- the menu -------------------------------------------------------------------------------------
await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
await page.getByRole("menuitem", { name: "Dashboard" }).waitFor({ timeout: 15000 });
check("the dashboard is in the tools menu", true);

await page.getByRole("menuitem", { name: "Dashboard" }).click();

// --- a dashboard with something in it ---------------------------------------------------------------
const name = `smoke ${Date.now().toString(36)}`;

await page.getByRole("button", { name: "New dashboard" }).click();
await page.getByLabel("Name").fill(name);

// The editor already holds one tile: fill it in without hunting for a second button.
await page.getByLabel("Title of tile 1").fill("Customers");
await page.getByLabel("Statement of tile 1").fill("SELECT count(*) FROM customers");
await page.getByRole("button", { name: "Save", exact: true }).click();

await page.getByText("Customers", { exact: true }).waitFor({ timeout: 20000 });
check("a tile can be filled in while the dashboard is created", true);

// The number itself is what a "number" tile is for.
await page.getByText(/^\d+$/).first().waitFor({ timeout: 20000 });
check("and it shows what its statement returned", true);

// --- and a second tile afterwards ---------------------------------------------------------------------
await page.getByRole("button", { name: "Edit tiles" }).click();
await page.getByRole("button", { name: "Add a tile" }).click();
await page.getByLabel("Title of tile 2").fill("Orders");
await page.getByLabel("Statement of tile 2").fill("SELECT count(*) FROM orders");
await page.getByRole("button", { name: "Save", exact: true }).click();

await page.getByText("Orders", { exact: true }).waitFor({ timeout: 20000 });
check("a second tile can be added afterwards", true);

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-dashboard.png" });
await browser.close();
console.log("dashboard check passed");
