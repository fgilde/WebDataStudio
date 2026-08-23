// Browser check for the features taken from DbGate: the filter language, the distinct-value list,
// a borrowed column, the perspective panel, archives, the map view and NOT EXISTS in the builder.
// Needs a running server with a PostgreSQL connection holding the shape the checks name
// (see docs/guide/development.md). BASE_URL defaults to :5005.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const connections = await (await page.request.get(`${baseUrl}/api/connections`)).json();
const server = connections.find(connection => connection.engine === "postgresql");

if (!server) {
  console.log("skipped: this smoke needs a PostgreSQL connection");
  await browser.close();
  process.exit(0);
}

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });

// --- the filter language, through the endpoint the data tab uses -------------------------------
const rows = async (query) =>
  (await (await page.request.get(`${baseUrl}/api/data/${server.id}?${query}`)).json()).rows;

const customers = "ref=Table:public/customers";

check("a plain word still contains",
  (await rows(`${customers}&filterColumn=name&filter=ad`)).length === 1);
check("and it does not care about case",
  (await rows(`${customers}&filterColumn=name&filter=ADA`)).length === 1);
check("^ starts with", (await rows(`${customers}&filterColumn=name&filter=${encodeURIComponent("^L")}`)).length === 1);
check("~ does not contain, and keeps the rows with no value",
  (await rows(`${customers}&filterColumn=city&filter=${encodeURIComponent("~lisbon")}`)).length === 2);
check("NULL asks for the rows with nothing in them",
  (await rows(`${customers}&filterColumn=city&filter=NULL`)).length === 1);
check("a number compares as a number",
  (await rows(`ref=Table:public/orders&filterColumn=total&filter=${encodeURIComponent(">9")}`)).length === 2);
check("a space is AND",
  (await rows(`ref=Table:public/orders&filterColumn=total&filter=${encodeURIComponent(">6 <50")}`)).length === 1);
check("a comma is OR",
  (await rows(`ref=Table:public/orders&filterColumn=total&filter=${encodeURIComponent("=5,=99")}`)).length === 2);
check("a date period is a period",
  (await rows(`ref=Table:public/orders&filterColumn=placed&filter=2026-01`)).length === 1);

// --- the data tab: the syntax hint, the distinct list, a borrowed column -----------------------
await page.getByText(server.name, { exact: true }).first().click();
await page.getByText("public", { exact: true }).first().click();
await page.getByText("Tables", { exact: true }).first().click();
await page.getByText("orders", { exact: true }).first().dblclick();

await page.getByRole("button", { name: "Insert row" }).waitFor({ timeout: 20000 });

// Scoped to the data panel: "customer_id" is also a node in the explorer, and the first match on
// the page is not the column header.
const data = page.locator(".dv-groupview")
  .filter({ has: page.getByRole("button", { name: "Insert row" }) }).first();
const header = (name) => data.locator("thead").getByText(name, { exact: true }).first();

await header("customer_id").click();

await page.getByText("hover for all", { exact: false }).first().waitFor({ timeout: 15000 });
check("the filter box says what it can do", true);

// The values the column actually holds, with their counts.
await page.getByRole("checkbox", { name: /^1 / }).first().waitFor({ timeout: 15000 });
check("the distinct values are listed with their counts", true);

await page.getByRole("checkbox", { name: /^1 / }).first().check();
await page.getByRole("button", { name: /Filter by/ }).click();
await page.waitForTimeout(800);
check("ticking a value filters by it",
  (await data.locator("tbody tr").count()) === 2);

// A column from the other side of the key.
await header("customer_id").click();
await page.getByText("from customers").waitFor({ timeout: 15000 });
await page.getByRole("menuitem", { name: "name" }).first().click();
await page.getByText("borrowed").waitFor({ timeout: 15000 });
check("a column can be borrowed from the table the key points at", true);
check("and it carries the value from over there",
  (await data.locator("tbody").first().innerText()).includes("Ada"));

// --- the perspective panel ---------------------------------------------------------------------
await page.getByRole("button", { name: "Perspective" }).click();
await page.getByPlaceholder("Start from").waitFor({ timeout: 20000 });
await page.getByPlaceholder("Start from").fill("public.customers");
await page.getByRole("option", { name: "public.customers" }).click();
await page.getByText("name=Ada", { exact: false }).first().waitFor({ timeout: 20000 });
await page.getByText("name=Ada", { exact: false }).first().click();
await page.getByText("orders (customer_id)").first().waitFor({ timeout: 15000 });
check("a row lists what points back at it", true);

await page.getByText("orders (customer_id)").first().click();
await page.getByText("total=", { exact: false }).first().waitFor({ timeout: 20000 });
check("and opening it shows those rows nested", true);

// --- archives -----------------------------------------------------------------------------------
const kept = await page.request.post(`${baseUrl}/api/archives/smoke-customers`, {
  data: { connectionId: server.id, sql: "SELECT id, name FROM customers ORDER BY id" },
});
check("a result can be kept as a file", kept.ok());

await page.getByRole("button", { name: "Archives" }).click();
await page.getByText("smoke-customers").first().waitFor({ timeout: 20000 });
await page.getByText("smoke-customers").first().click();
await page.getByText("3 rows").first().waitFor({ timeout: 20000 });
check("the archive panel lists it and opens its rows", true);

await page.getByRole("button", { name: "Script the rows as INSERTs…" }).click();
await page.getByLabel("Table").fill("public.customers_copy");
await page.getByRole("button", { name: "Build the script" }).click();
await page.getByText("INSERT INTO public.customers_copy", { exact: false })
  .first().waitFor({ timeout: 20000 });
check("and scripts them back out as INSERTs", true);

// The script opened a query tab, which took the focus away from the archive panel.
await page.getByRole("button", { name: "Archives" }).click();
await page.getByLabel("Delete smoke-customers").click();
await page.waitForTimeout(500);
const remaining = await (await page.request.get(`${baseUrl}/api/archives`)).json();
check("deleting an archive removes the file",
  !remaining.items.some(item => item.name === "smoke-customers"));

// --- the map ------------------------------------------------------------------------------------
await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").last().waitFor({ timeout: 20000 });
await page.locator(".monaco-editor").last().click();
await page.keyboard.type("SELECT name, lat, lon FROM places ORDER BY name");
await page.keyboard.press("F5");
await page.getByText("Berlin", { exact: true }).first().waitFor({ timeout: 20000 });

// Mantine's SegmentedControl hides the radio itself; the label is what a person clicks.
await page.getByText("Map", { exact: true }).first().click();
await page.getByRole("img", { name: "the result's geography" }).waitFor({ timeout: 15000 });
check("a latitude and longitude pair is drawn on the map", true);
check("and it says how it recognised them",
  await page.getByText("from lat / lon", { exact: false }).isVisible());

// --- NOT EXISTS in the builder ------------------------------------------------------------------
await page.getByRole("button", { name: "Query builder" }).click();
await page.getByPlaceholder("Add a table").waitFor({ timeout: 20000 });
await page.getByPlaceholder("Add a table").click();
await page.getByRole("option", { name: "customers" }).first().click();
await page.waitForTimeout(700);

await page.getByLabel("Add Exists").click();
// A Mantine Select keeps its value in the input, not as text on the page.
await page.locator('input[value="NOT EXISTS"]').first().waitFor({ timeout: 15000 });
check("the builder can ask for the rows with nothing on the other side", true);

const filtered = errors.filter(text => !text.includes("favicon"));
check("no console errors", filtered.length === 0);
if (filtered.length > 0) console.log(filtered.join("\n"));

await browser.close();
console.log("smoke-dbgate passed");
