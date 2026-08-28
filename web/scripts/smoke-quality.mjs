// Browser check for the newer panels: what is inside a JSON column, rules about the data, who did
// what, and a development subset.
//
// Needs a running server with the demo data seeded (see docs/guide/development.md). BASE_URL
// defaults to :5005.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });

// A 4xx from a panel an engine cannot answer is an answer this app renders, not a failure: SQLite
// really has no server statistics. Only unexpected console errors count here.
const expected = /status of (400|404)/;
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const connections = await (await page.request.get(`${baseUrl}/api/connections`)).json();
const demo = connections.find(connection => connection.engine === "sqlite")
  ?? connections.find(connection => !["storage", "redis", "mongodb"].includes(connection.engine));

check("there is a connection to work with", Boolean(demo));

// --- rules about the data, through the API the panel calls ---------------------------------------
const rule = async body => {
  const response = await page.request.put(`${baseUrl}/api/quality/${demo.id}`, { data: body });
  check(`saved a ${body.kind} rule`, response.ok());
  return (await response.json()).id;
};

// Whatever a previous run left behind: the counts below are about this run's own rules.
for (const existing of await (await page.request.get(`${baseUrl}/api/quality/${demo.id}`)).json())
  await page.request.delete(`${baseUrl}/api/quality/${demo.id}/${existing.id}`);

const notNull = await rule({
  id: "", connectionId: demo.id, schema: "", table: "people", column: "city",
  kind: "NotNull", argument: null, message: "every person needs a city", enabled: true,
});

const range = await rule({
  id: "", connectionId: demo.id, schema: "", table: "orders", column: "total",
  kind: "Range", argument: "0..100000", message: null, enabled: true,
});

const report = await (await page.request.post(`${baseUrl}/api/quality/${demo.id}/run`)).json();

check("both rules ran", report.ran === 2);
check("each result carries its counting statement",
  report.results.every(result => typeof result.statement === "string"));

// --- the panel itself ----------------------------------------------------------------------------
await page.goto(`${baseUrl}/`, { waitUntil: "networkidle" });

// The tools menu opens the administration panel; the tab is picked once it is open. Both the header
// and the explorer carry the menu — the same registry rendered twice — so the header's is named.
await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
await page.getByRole("menuitem", { name: /Administration/ }).first().click();
await page.waitForTimeout(1500);

const qualityTab = page.getByRole("tab", { name: "Data quality" });
check("the admin panel has a data quality tab", await qualityTab.count() > 0);
await qualityTab.first().click();
await page.waitForTimeout(800);

check("the rules are listed", await page.getByText("every person needs a city").count() > 0);

await page.getByRole("button", { name: "Run now" }).click();
await page.waitForTimeout(1500);

check("a result is shown per rule",
  (await page.getByText(/rows|ok/).count()) > 0);

// --- who did what --------------------------------------------------------------------------------
const auditTab = page.getByRole("tab", { name: "Audit" });
check("the admin panel has an audit tab", await auditTab.count() > 0);
await auditTab.first().click();
await page.waitForTimeout(1000);

check("the trail carries the runs this smoke caused",
  await page.getByText(/quality\/run|query\/execute/).count() > 0);

// --- a development subset ------------------------------------------------------------------------
const subset = await (await page.request.post(`${baseUrl}/api/export/subset/${demo.id}`, {
  data: { table: "orders", rows: 5 },
})).json();

check("the subset script is a script", (subset.script ?? "").includes("INSERT INTO"));
check("it names what it took", Array.isArray(subset.tables) && subset.tables.length > 0);
check("it says where it came from", (subset.script ?? "").includes("WebDataStudio"));

// --- what is inside a JSON column ----------------------------------------------------------------
// The demo data has one document column, with the same paths in most rows, one extra path and one
// nested object — so the report has something to be honest about.
const events = (await (await page.request.get(
  `${baseUrl}/api/schema/${demo.id}?parent=${encodeURIComponent("TableFolder:main/tables")}`)).json())
  .find(node => node.ref.endsWith("/events"));

if (events) {
  const shape = await (await page.request.get(
    `${baseUrl}/api/data/${demo.id}/json?ref=${encodeURIComponent(events.ref)}&column=payload`)).json();

  check("every document was read", shape.sampled === shape.parsed && shape.sampled > 0);

  const paths = shape.paths.map(path => path.path);
  check("the paths every row has come first", paths[0] === "plan" && paths[1] === "seats");
  check("a path only one row has is there too", paths.includes("note"));
  check("a nested path is named by its parent", paths.includes("refund.amount"));
  check("an array's items are named once", paths.includes("tags[]"));

  // Only value paths become columns: a column cannot hold a subtree.
  check("the flatten statement leaves the subtrees out",
    shape.flatten.includes("\"plan\"") && !shape.flatten.includes("AS \"refund\","));
} else {
  console.log("skipped the JSON shape: this demo data has no events table");
}

// --- tidy up -------------------------------------------------------------------------------------
for (const id of [notNull, range])
  check(`removed rule ${id}`,
    (await page.request.delete(`${baseUrl}/api/quality/${demo.id}/${id}`)).ok());

if (errors.length > 0) console.log("console errors:", errors.slice(0, 5));
check("no console errors", errors.length === 0);

await browser.close();
console.log("smoke-quality: done");
