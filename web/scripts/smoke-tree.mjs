// Browser check for the explorer: a table expands into its parts, the context menu is the one
// that fits the node, and an index can be created from it end to end.
// Needs a running server with a writable demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];
const expected = /status of (400|404)/;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-tree-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 600));
  throw error;
};

const node = (label) =>
  page.locator(".mantine-UnstyledButton-root").filter({ hasText: label }).first();

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).click();
await page.getByText("Tables", { exact: true }).click();

// --- a table expands into columns, indexes and keys ------------------------------------
try {
  await node(/^orders$/).click();
  await node(/^person_id/).waitFor({ timeout: 15000 });
  await page.getByText(/→ people/).first().waitFor({ timeout: 10000 });
} catch (e) { await fail("expand", e); }

// --- the menu fits the node ----------------------------------------------------------------
try {
  await node(/^total/).click({ button: "right" });
  await page.getByText("Add index on this column…").waitFor({ timeout: 10000 });
  await page.getByText("Script: DROP COLUMN").waitFor({ timeout: 5000 });
  await page.keyboard.press("Escape");
} catch (e) { await fail("column-menu", e); }

try {
  await node(/^orders$/).click({ button: "right" });
  await page.getByText("Indexes…").first().waitFor({ timeout: 10000 });
  await page.getByText("Script: TRUNCATE").waitFor({ timeout: 5000 });
} catch (e) { await fail("table-menu", e); }

// --- create an index through the designer's index tab --------------------------------------
const indexName = `ix_smoke_${Date.now()}`;

// A run that failed part-way leaves its index behind, and a second index over the same column is
// nothing to change — which would fail this check for the wrong reason. So every index this smoke
// ever made is dropped first.
{
  const id = await connectionId();
  const children = await (await page.request.get(
    `${baseUrl}/api/schema/${id}?parent=${encodeURIComponent("Table:main/orders")}`)).json();

  for (const leftover of children.filter(node => node.label.startsWith("ix_smoke_")))
    await page.request.post(`${baseUrl}/api/query/execute`, {
      data: { connectionId: id, sql: `DROP INDEX "${leftover.label}"` },
    });
}
try {
  await page.getByText("Indexes…").first().click();
  await page.getByText(/^Indexes of/).waitFor({ timeout: 15000 });
  await page.getByRole("button", { name: "Add index", exact: true }).click();

  // The row that was just added: the last one in the tab that is open. The designer keeps every tab
  // panel mounted, so an unscoped locator lands in a hidden one — and matching on the generated name
  // through the value attribute does not work either, because React sets it as a property.
  const row = page.locator('[role="tabpanel"]:visible tbody tr').last();

  await row.locator(".mantine-MultiSelect-input").click();
  // Only the dropdown that is open: the designer keeps every row's select mounted, so an option
  // matched anywhere on the page can be one nobody can click.
  // A column the table is not already indexed on: the designer treats a second index over the same
  // columns as nothing to change, which is right and would make this check prove nothing.
  await page.locator("[role=option]:visible").filter({ hasText: "placed" }).first().click();
  await page.keyboard.press("Escape");

  await row.locator("input").first().fill(indexName);

  await page.locator(".mantine-Modal-content").getByRole("button", { name: "Save", exact: true }).click();
  await page.getByText(/CREATE INDEX/i).first().waitFor({ timeout: 15000 });
  await page.getByRole("dialog").filter({ hasText: "Migration preview" })
    .getByRole("button", { name: "Apply", exact: true }).click();
  await page.waitForTimeout(1500);
} catch (e) { await fail("index", e); }

// The index is only really there if the server reports it back on the table.
const children = await page.request.get(
  `${baseUrl}/api/schema/${await connectionId()}?parent=${encodeURIComponent("Table:main/orders")}`);
const labels = (await children.json()).map(n => n.label);

if (!labels.includes(indexName)) {
  await fail("verify", new Error(`the index is not on the table: ${labels.join(", ")}`));
}

async function connectionId() {
  const response = await page.request.get(`${baseUrl}/api/connections`);
  const connections = await response.json();

  // The demo database, not whichever connection happens to be first: a studio with a bucket attached
  // lists that one too, and a bucket has no tables to index.
  return (connections.find(c => c.name === "DEMO") ?? connections[0]).id;
}

await page.screenshot({ path: "smoke-tree.png" });

// Drop what this run created, so the next one starts from the same table.
await page.request.post(`${baseUrl}/api/query/execute`, {
  data: { connectionId: await connectionId(), sql: `DROP INDEX "${indexName}"` },
});

await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-tree ok");
