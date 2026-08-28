// Browser check for the accounts panel: a role created, listed, and dropped again — each one
// through the statement the studio shows first. Needs a running server with a connection whose
// engine has accounts (PostgreSQL, MySQL, SQL Server); without one the script says so and stops.
// BASE_URL defaults to :5005.
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

const connections = await (await page.request.get(`${baseUrl}/api/connections`)).json();
const server = connections.find(c => ["postgresql", "mysql", "sqlserver"].includes(c.engine));

if (!server) {
  console.log("skipped: no connection here has accounts to manage");
  await browser.close();
  process.exit(0);
}

const role = `smoke_role_${Date.now().toString(36)}`;

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText(server.name, { exact: true }).first().click();

await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
await page.getByRole("menuitem", { name: /^Administration/ }).first().click();

await page.getByRole("tab", { name: "Accounts" }).click();
await page.getByRole("button", { name: "New role…" }).waitFor({ timeout: 20000 });

// --- what is there already ----------------------------------------------------------------------
const listed = await page.locator("table").first().innerText();
check("the panel lists what the server knows", listed.length > 0);

// --- a role, created through the statement it shows ------------------------------------------------
await page.getByRole("button", { name: "New role…" }).click();
await page.getByLabel("Name").fill(role);
await page.getByRole("button", { name: "Show the statement…" }).click();

const script = await page.getByText("CREATE ROLE", { exact: false }).first().innerText();
check("the statement is shown before anything runs", script.includes(role));

await page.getByRole("button", { name: "Run it" }).click();
await page.getByText(role, { exact: true }).first().waitFor({ timeout: 20000 });
check("and the role is in the list afterwards", true);

// --- and dropped the same way ----------------------------------------------------------------------
await page.getByLabel(`Change ${role}`).click();
await page.getByText("Drop…", { exact: true }).click();
await page.getByText("DROP ROLE", { exact: false }).first().waitFor({ timeout: 15000 });
await page.getByRole("button", { name: "Run it" }).click();

await page.getByText(role, { exact: true }).first().waitFor({ state: "detached", timeout: 20000 });
check("and gone after the drop", true);

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-security.png" });
await browser.close();
console.log("security smoke passed");
