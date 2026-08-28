// Browser check for the visual query builder: tables land on a canvas, the join between them is
// proposed from the foreign key rather than typed, and the first rows of the query being built are
// visible while it is built.
// Needs a running server with the demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 20000 });

// The tools live in one menu now, rendered by the header and by the explorer from the same
// registry — the explorer's icon row was cut off at any sensible width.
await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
await page.getByRole("menuitem", { name: /Query builder/ }).first().click();
const addTable = page.getByPlaceholder("Add a table");
await addTable.waitFor({ timeout: 20000 });

const pick = async (table) => {
  await addTable.click();
  await page.getByRole("option", { name: table, exact: true }).click();
  await page.waitForTimeout(600);
};

await pick("people");
check("the table shows up as a card on the canvas",
  await page.locator(".react-flow__node").count() === 1);

await pick("orders");
check("the second table joins it on the canvas",
  await page.locator(".react-flow__node").count() === 2);

// The foreign key orders.person_id → people.id is what this is about: the join is there without
// anybody typing a condition.
const edges = await page.locator(".react-flow__edge").count();
check(`the join is proposed from the foreign key (${edges} edge)`, edges === 1);

// Pick a column on each card, which is what makes the query runnable.
await page.locator(".react-flow__node").first().getByText("name", { exact: true }).click();
await page.locator(".react-flow__node").nth(1).getByText("total", { exact: true }).click();
await page.waitForTimeout(1200);

const sql = await page.locator("pre").first().innerText();
check(`the generated SQL joins both tables (${sql.split("\n")[0]})`,
  /JOIN/i.test(sql) && /people/i.test(sql) && /orders/i.test(sql));

// And the preview under it shows real rows from those tables. Asserted on the row count rather
// than on a value: the editing smoke renames rows in the same demo database.
const builder = page.locator(".dv-groupview").filter({ has: addTable }).first();
await builder.getByText(/\d+ rows/).first().waitFor({ timeout: 20000 });
const preview = await builder.innerText();
check(`the preview shows rows while the query is being built (${/(\d+) rows/.exec(preview)?.[0]})`,
  // A row count and a total that looks like money: naming one value would tie this to a seed the
  // editing smoke is allowed to change.
  /[1-9]\d* rows/.test(preview) && /\d+\.\d/.test(preview));

// The generated statement carries its model, so the query can come back into the builder.
await page.getByRole("button", { name: "Open in query tab" }).click();
await page.locator(".monaco-editor").first().waitFor({ timeout: 20000 });

await page.keyboard.press("Control+k");
await page.getByPlaceholder(/command/i).fill("builder");
await page.getByText("Open this query in the builder").click();
await page.waitForTimeout(1500);

const builders = await page.getByRole("tab", { name: "Builder" }).count();
check(`the query reopens in a builder (${builders} builder tabs)`, builders >= 2);
check("the reopened builder shows both tables again",
  await page.locator(".react-flow__node").count() >= 2);

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-builder.png" });
await browser.close();
console.log("builder smoke passed");
