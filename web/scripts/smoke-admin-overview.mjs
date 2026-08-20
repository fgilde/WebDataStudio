// Browser check for the administration overview: the tiles that say what is happening now, the
// blocking tree, the size treemap, and a finding that can be applied.
// Needs a running server with a PostgreSQL connection (the tab is capability-gated, and SQLite
// answers none of these). BASE_URL defaults to :5005.
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
const server = connections.find(connection =>
  ["postgresql", "mysql", "sqlserver"].includes(connection.engine));

if (!server) {
  console.log("skipped: this studio has no server-based connection (PostgreSQL, MySQL, SQL Server)");
  await browser.close();
  process.exit(0);
}

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText(server.name, { exact: true }).first().click();
await page.waitForTimeout(400);

await page.getByRole("button", { name: "Administration" }).click();
const panel = page.locator(".dv-groupview").filter({ has: page.getByRole("tab", { name: "Overview" }) }).first();
await page.getByRole("tab", { name: "Overview" }).waitFor({ timeout: 20000 });
await page.waitForTimeout(2500);

const overview = await panel.innerText();
check("the tiles report connections and what is running",
  overview.includes("CONNECTIONS") && overview.includes("RUNNING") && overview.includes("WAITING"));
check("the running list is there", overview.includes("Running now"));

// A sparkline appears once there is more than one sample, which is what the history is for.
await page.waitForTimeout(5200);
check("the tiles draw a history",
  await panel.locator("svg path").count() > 0);

// --- the treemap ------------------------------------------------------------------------------
await page.getByRole("tab", { name: "Databases" }).click();
await page.waitForTimeout(1500);
check("the databases tab draws the sizes",
  (await panel.innerText()).includes("Size by database")
  && await panel.locator("svg rect").count() > 0);

// --- replication ------------------------------------------------------------------------------
await page.getByRole("tab", { name: "Replication" }).click();
await page.waitForTimeout(1200);
check("replication says something either way", (await panel.innerText()).length > 0);

// --- a finding that can be applied ------------------------------------------------------------
await page.getByRole("tab", { name: "Health" }).click();
await page.waitForTimeout(3000);
const health = page.locator(".dv-groupview").filter({ has: page.getByRole("button", { name: "Re-run" }) }).first();
const findings = await health.innerText();

if (findings.includes("Apply this…")) {
  await health.getByRole("button", { name: "Apply this…" }).first().click();
  await page.getByText("Apply this fix?").waitFor({ timeout: 10000 });
  check("a finding's fix opens as a previewed script",
    (await page.locator(".mantine-Modal-content").innerText()).length > 10);
  await page.keyboard.press("Escape");
} else {
  console.log("ok   no finding on this database has a fix to apply (nothing to check)");
}

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-admin-overview.png" });
await browser.close();
console.log("admin overview smoke passed");
