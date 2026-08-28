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
// The seed's policy compares against a setting rather than a literal, which is what a real
// multi-tenant policy looks like.
check("its expression is shown",
  await page.getByText("current_setting", { exact: false }).first().isVisible());

// A policy is SQL: the button builds the statement and hands it to the editor rather than running it.
// The seed has two policies on this table; either one proves the point.
await page.getByLabel("Drop policy").first().click();
// The statement lands in a query tab; which editor on screen holds it is not the point.
await page.getByText("DROP POLICY", { exact: false }).first().waitFor({ timeout: 15000 });
check("dropping a policy opens the statement instead of running it", true);

check("the policies are still there, because nothing ran",
  (await (await page.request.get(
    `${baseUrl}/api/schema/${server.id}/policies?ref=Table:public/tenants`)).json())
    .policies.length === 2);

// --- partitions -----------------------------------------------------------------------------
await page.getByText("events", { exact: true }).first().click();
await page.getByRole("tab", { name: "Partitions" }).click();
await page.getByText("RANGE", { exact: true }).waitFor({ timeout: 10000 });
check("the partitions tab names the strategy and the key", true);
// The partition is both a row in the table and a node in the tree; the table is the tab's answer.
check("the partitions are listed",
  await page.getByRole("cell", { name: "events_2026_06" }).isVisible());
check("the bound is shown as PostgreSQL spells it",
  await page.getByText("FOR VALUES FROM ('2026-06-01')", { exact: false }).first().isVisible());

// A table that is not partitioned says so rather than showing an empty list.
await page.getByText("customers", { exact: true }).first().click();
await page.getByRole("tab", { name: "Partitions" }).click();
await page.getByText("This table is not partitioned.").waitFor({ timeout: 10000 });
check("an unpartitioned table says so", true);

// --- the function inspector ------------------------------------------------------------------
await page.getByText("Functions", { exact: true }).first().click();
await page.getByText("spent_by", { exact: true }).first().click();
await page.getByRole("tab", { name: "Inspect" }).click();
await page.getByText("plpgsql", { exact: true }).waitFor({ timeout: 10000 });
check("the inspector names the language and the return type", true);
check("the declared parameter is a field", await page.getByLabel("p_country — text").isVisible());

await page.getByLabel("p_country — text").fill("GB");
await page.getByRole("button", { name: "Run and roll back" }).click();
// The notice the function raises on the way is the point: it is shown, not swallowed.
await page.getByText("adding up orders for GB", { exact: false }).waitFor({ timeout: 15000 });
check("the run shows what the function raised", true);

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
await page.getByText("order_totals", { exact: true }).first().click({ button: "right" });
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

// --- the objects a table designer never covered ---------------------------------------------------
// A view written in the editor, read back, and dropped again - all of it through the preview that
// every other change goes through.
{
  const name = `smoke_view_${Date.now().toString(36)}`;

  await page.getByText("Views", { exact: true }).first().click({ button: "right" });
  await page.getByText("New view…", { exact: true }).click();

  await page.getByLabel("Name").fill(name);
  await page.locator(".mantine-Modal-content .monaco-editor").last().click();
  await page.keyboard.press("Control+A");
  await page.keyboard.type("SELECT id FROM orders");

  await page.getByRole("button", { name: "Save…" }).click();

  const script = await page.getByText("CREATE OR REPLACE VIEW", { exact: false }).first().innerText();
  check("the view is shown as the statement it will run", script.includes(name));

  await page.getByRole("button", { name: "Run it" }).click();
  await page.waitForTimeout(1500);

  const created = await (await page.request.get(
    `${baseUrl}/api/ddl/${server.id}?ref=${encodeURIComponent(`View:public/${name}`)}`)).json();
  check("and it is there afterwards", (created.create ?? "").includes(name));

  // Dropping goes the same way: the statement first, the database second. The tree reloads itself
  // after an applied change; the folder still has to be open for the new view to be in view.
  if (await page.getByText(name, { exact: true }).count() === 0)
    await page.getByText("Views", { exact: true }).first().click();

  await page.getByText(name, { exact: true }).first().waitFor({ timeout: 20000 });
  await page.getByText(name, { exact: true }).first().click({ button: "right" });
  await page.getByText("Drop…", { exact: true }).click();
  await page.getByText("DROP VIEW", { exact: false }).first().waitFor({ timeout: 15000 });
  await page.getByRole("button", { name: "Run it" }).click();
  await page.waitForTimeout(1500);

  // The tree reloaded what was open rather than starting over, so the view is simply not in it
  // any more - and everything somebody had expanded is still expanded.
  await page.getByText(name, { exact: true }).first().waitFor({ state: "detached", timeout: 20000 });
  check("and gone from the tree afterwards, without it collapsing",
    await page.getByText("Views", { exact: true }).first().isVisible());

  const views = await (await page.request.get(
    `${baseUrl}/api/schema/${server.id}?parent=${encodeURIComponent("ViewFolder:public")}`)).json();
  check("and gone from the database", !JSON.stringify(views).includes(name));
}

const filtered = errors.filter(text => !text.includes("favicon"));
check("no console errors", filtered.length === 0);
if (filtered.length > 0) console.log(filtered.join("\n"));

await browser.close();
console.log("smoke-objects passed");
