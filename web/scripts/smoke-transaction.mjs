// Browser check for the query tab's seatbelt: a transaction opened by hand, a statement run inside
// it, and a rollback that leaves nothing behind — plus the pivot view over the rows on screen.
// Needs a running server with a connection whose engine has transactions. BASE_URL defaults to :5005.
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
const server = connections.find(c => ["postgresql", "mysql", "sqlserver", "sqlite"].includes(c.engine));

if (!server) {
  console.log("skipped: no connection here has transactions");
  await browser.close();
  process.exit(0);
}

const table = `smoke_tx_${Date.now().toString(36)}`;
const run = (sql, extra = {}) => page.request.post(`${baseUrl}/api/query/execute`, {
  data: { connectionId: server.id, sql, ...extra },
});

await run(`CREATE TABLE ${table} (id int, kind text, amount int)`);
await run(`INSERT INTO ${table} VALUES (1, 'a', 10), (2, 'b', 20), (3, 'a', 30)`);

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText(server.name, { exact: true }).first().click();

await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").last().waitFor({ timeout: 20000 });

// --- the seatbelt ---------------------------------------------------------------------------------
await page.getByRole("button", { name: "Begin" }).click();
await page.getByText(/transaction · \d+ run/).waitFor({ timeout: 15000 });
check("a transaction can be opened by hand", true);

await page.locator(".monaco-editor").last().click();
// With a WHERE: a sweep would open the pre-run inspection dialog, which is a different
// check than this one.
await page.keyboard.type(`DELETE FROM ${table} WHERE id = 1`);
await page.keyboard.press("F5");
await page.waitForTimeout(1500);

// Inside the transaction the rows are gone; outside — a separate request — they are still there.
const during = await (await run(`SELECT count(*) AS n FROM ${table}`)).text();
check("and what runs inside it is not written yet", during.includes("3"));

await page.getByRole("button", { name: "Rollback" }).click();
await page.getByRole("button", { name: "Begin" }).waitFor({ timeout: 15000 });

const after = await (await run(`SELECT count(*) AS n FROM ${table}`)).text();
check("a rollback leaves nothing behind", after.includes("3"));

// --- the pivot ------------------------------------------------------------------------------------
await page.locator(".monaco-editor").last().click();
await page.keyboard.press("Control+A");
await page.keyboard.type(`SELECT kind, id, amount FROM ${table}`);
await page.keyboard.press("F5");
await page.waitForTimeout(1500);

await page.getByText("Pivot", { exact: true }).click();
await page.getByRole("combobox", { name: "Aggregate" }).waitFor({ timeout: 15000 });

const pivot = await page.locator("table").last().innerText();
check("the pivot crosses two columns of the result", pivot.includes("all"));
check("and counts what falls in each cell", /\b3\b/.test(pivot));

await run(`DROP TABLE ${table}`);

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-transaction.png" });
await browser.close();
console.log("transaction smoke passed");
