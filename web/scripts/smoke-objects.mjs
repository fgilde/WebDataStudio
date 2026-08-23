// Browser check for the object tabs added on top of the structure panel: row-level security,
// partitions, a function run that is rolled back — plus the preferences dialog and its rebinding.
// Needs a running server with a PostgreSQL connection that has the objects the checks name
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
  console.log("skipped: these tabs are PostgreSQL-only and this studio has no such connection");
  await browser.close();
  process.exit(0);
}

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });

// The tree, down to a table. Each level is its own request, so each click waits for the next.
await page.getByText(server.name, { exact: true }).first().click();
await page.getByText("public", { exact: true }).first().click();
await page.getByText("Tables", { exact: true }).first().click();

// --- policies -------------------------------------------------------------------------------
await page.getByText("tenants", { exact: true }).first().click();
await page.getByRole("tab", { name: "Policies" }).click();
await page.getByText("row security on").waitFor({ timeout: 10000 });
check("the policies tab says security is on", true);
check("the policy is listed", await page.getByText("own_rows").isVisible());
check("its expression is shown", await page.getByText("tenant_id = 1").first().isVisible());

// A policy is SQL: the button builds the statement and hands it to the editor rather than running it.
await page.getByLabel("Drop policy").click();
// The statement lands in a query tab; which editor on screen holds it is not the point.
await page.getByText("DROP POLICY", { exact: false }).first().waitFor({ timeout: 15000 });
check("dropping a policy opens the statement instead of running it", true);

check("the policy is still there, because nothing ran",
  (await (await page.request.get(
    `${baseUrl}/api/schema/${server.id}/policies?ref=Table:public/tenants`)).json())
    .policies.length === 1);

// --- partitions -----------------------------------------------------------------------------
await page.getByText("events", { exact: true }).first().click();
await page.getByRole("tab", { name: "Partitions" }).click();
await page.getByText("RANGE", { exact: true }).waitFor({ timeout: 10000 });
check("the partitions tab names the strategy and the key", true);
// The partition is both a row in the table and a node in the tree; the table is the tab's answer.
check("both partitions are listed",
  await page.getByRole("cell", { name: "events_2026_02" }).isVisible());
check("the bound is shown as PostgreSQL spells it",
  await page.getByText("FOR VALUES FROM ('2026-02-01')").isVisible());

// A table that is not partitioned says so rather than showing an empty list.
await page.getByText("numbers", { exact: true }).first().click();
await page.getByRole("tab", { name: "Partitions" }).click();
await page.getByText("This table is not partitioned.").waitFor({ timeout: 10000 });
check("an unpartitioned table says so", true);

// --- the function inspector ------------------------------------------------------------------
await page.getByText("Functions", { exact: true }).first().click();
await page.getByText("add_up", { exact: true }).first().click();
await page.getByRole("tab", { name: "Inspect" }).click();
await page.getByText("plpgsql", { exact: true }).waitFor({ timeout: 10000 });
check("the inspector names the language and the return type", true);
check("the declared parameter is a field", await page.getByLabel("p_factor — integer").isVisible());

await page.getByLabel("p_factor — integer").fill("3");
await page.getByRole("button", { name: "Run and roll back" }).click();
await page.getByText("counting with factor 3").waitFor({ timeout: 15000 });
check("the run shows what the function raised", true);
check("and what it returned", await page.getByRole("cell", { name: "3825" }).isVisible());

// --- the schema-wide grant, and refreshing a materialised view -------------------------------
await page.getByText("public", { exact: true }).first().click({ button: "right" });
await page.getByText("Privileges on everything here…").click();
await page.getByLabel("Role").fill("reporting");
await page.getByRole("button", { name: "Build the script" }).click();
await page.getByText("ON ALL TABLES IN SCHEMA", { exact: false }).first().waitFor({ timeout: 15000 });
check("a schema-wide grant opens as one script", true);
check("and covers the tables created later too",
  await page.getByText("ALTER DEFAULT PRIVILEGES", { exact: false }).first().isVisible());

// A materialised view lives in the Views folder with its own kind, so its menu is not a view's.
await page.getByText("Views", { exact: true }).first().click();
await page.getByText("number_count", { exact: true }).first().click({ button: "right" });
await page.getByText("Script: REFRESH CONCURRENTLY").click();
await page.getByText("REFRESH MATERIALIZED VIEW CONCURRENTLY", { exact: false })
  .first().waitFor({ timeout: 15000 });
check("a materialised view can be refreshed without blocking readers", true);

// --- preferences ----------------------------------------------------------------------------
await page.keyboard.press("Control+Comma");
await page.getByText("Rows per page in the data tab").waitFor({ timeout: 10000 });
check("Ctrl+, opens the preferences", true);

await page.getByRole("tab", { name: "Keyboard" }).click();
const binding = page.getByRole("button", { name: "Ctrl+D" }).first();
await binding.click();
await page.keyboard.press("Control+Shift+G");
await page.getByRole("button", { name: "Ctrl+Shift+G" }).first().waitFor({ timeout: 10000 });
check("a command can be rebound", true);

// It is stored in the workspace, not in this page.
const stored = await (await page.request.get(`${baseUrl}/api/workspace/item/preferences`)).json();
check("the new binding is in the workspace", stored?.shortcuts?.["tool.diagram"] === "Ctrl+Shift+G");

// Put it back, so a second run of this smoke starts where the first did.
await page.getByLabel("Reset binding").first().click();
await page.waitForTimeout(300);

// --- a history entry that keeps its result ---------------------------------------------------
await page.getByRole("tab", { name: "General" }).click();
await page.getByRole("switch", { name: "Keep the result with each history entry" }).check();
await page.waitForTimeout(300);
await page.keyboard.press("Escape");

await page.getByRole("button", { name: "New query" }).click();
await page.locator(".monaco-editor").last().waitFor({ timeout: 20000 });
await page.locator(".monaco-editor").last().click();
// No quotes and no brackets: Monaco closes pairs as they are typed.
await page.keyboard.type("SELECT 41 + 1 AS answer");
await page.keyboard.press("F5");
await page.getByText("42", { exact: true }).first().waitFor({ timeout: 20000 });

const entries = await (await page.request.get(`${baseUrl}/api/history?limit=5`)).json();
check("the run kept its result", entries[0].hasSnapshot === true);
check("and the rows are fetched separately",
  (await (await page.request.get(`${baseUrl}/api/history/${entries[0].id}/snapshot`)).json())
    .rows[0][0] === 42);

// Off again: a snapshot is a copy of the data, and this smoke should not leave that on.
await page.keyboard.press("Control+Comma");
await page.getByRole("switch", { name: "Keep the result with each history entry" }).uncheck();
await page.waitForTimeout(300);
await page.keyboard.press("Escape");

const filtered = errors.filter(text => !text.includes("favicon"));
check("no console errors", filtered.length === 0);
if (filtered.length > 0) console.log(filtered.join("\n"));

await browser.close();
console.log("smoke-objects passed");
