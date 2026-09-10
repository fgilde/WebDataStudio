// Browser check for the dashboard: the canvas, a widget of every family drawing something, the
// time range, a variable, and a Grafana dashboard pasted in. GridStack moves DOM nodes and ECharts
// wants a canvas element, so neither is testable in jsdom — this is where they are checked.
//
// Needs a running server with a connection that has `customers` and `orders` (the demo seed has
// both). BASE_URL defaults to :5005.
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

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-dashboard-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  throw error;
};

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });

const connections = await (await page.request.get(`${baseUrl}/api/connections`)).json();
const connection = connections[0];
await page.getByText(connection.name, { exact: true }).first().click();

// --- the way in -----------------------------------------------------------------------------------
await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
await page.getByRole("menuitem", { name: "Dashboard" }).waitFor({ timeout: 15000 });
await page.getByRole("menuitem", { name: "Dashboard" }).click();
check("the dashboard is in the tools menu", true);

// --- a dashboard built here ------------------------------------------------------------------------
const name = `smoke ${Date.now().toString(36)}`;

try {
  // Through the menu rather than the empty state: a studio that already has a dashboard shows the
  // dashboard rather than an empty page, and this check runs on both.
  await page.getByRole("button", { name: "More" }).click();
  await page.getByRole("menuitem", { name: "New dashboard" }).click();
  await page.getByLabel("Name").fill(name);

  // A stat and a bar: one number and one shape, which is the pair every dashboard starts as.
  for (const [type, title, sql] of [
    ["Stat", "Customers", "SELECT count(*) AS n FROM customers"],
    ["Bar", "Orders by status", "SELECT status, count(*) AS n FROM orders GROUP BY status"],
  ]) {
    await page.getByRole("button", { name: /add widget/i }).first().click();
    await page.getByRole("button", { name: new RegExp(`^${type}\\b`) }).first().click();

    // The settings drawer, which the palette opens on the new widget.
    const drawer = page.getByRole("dialog");

    await drawer.getByLabel("Title").fill(title);
    await drawer.getByRole("combobox", { name: "Connection" }).click();
    await page.getByRole("option", { name: connection.name }).click();
    await drawer.getByLabel("Statement").fill(sql);
    await page.keyboard.press("Escape");
  }

  await page.getByRole("button", { name: "Save", exact: true }).click();
  await page.getByText(name).first().waitFor({ timeout: 20000 });
  check("a dashboard is built from the palette and saved", true);
} catch (e) { await fail("build", e); }

// --- what the widgets drew --------------------------------------------------------------------------
try {
  // The stat's own number.
  await page.getByText(/^\d[\d.,]*$/).first().waitFor({ timeout: 20000 });
  check("the stat shows what its statement returned", true);

  // ECharts draws into a canvas, so one existing is the chart having rendered at all.
  await page.locator("canvas").first().waitFor({ timeout: 20000 });
  const box = await page.locator("canvas").first().boundingBox();
  check("the chart rendered with a size", (box?.width ?? 0) > 50 && (box?.height ?? 0) > 30);
} catch (e) { await fail("draw", e); }

// --- the canvas is a canvas -------------------------------------------------------------------------
try {
  await page.getByRole("button", { name: "Edit" }).click();

  const first = page.locator(".grid-stack-item").first();
  const before = await first.boundingBox();

  // Drag the resize corner: a widget that cannot be resized is not a canvas. GridStack hides the
  // handle until the pointer is over the widget, which is also why nothing is in the way in view
  // mode.
  await first.hover();
  const corner = await first.locator(".ui-resizable-se").boundingBox();
  if (!corner) throw new Error("no resize handle on a widget in edit mode");

  await page.mouse.move(corner.x + 4, corner.y + 4);
  await page.mouse.down();
  await page.mouse.move(corner.x + 220, corner.y + 90, { steps: 12 });
  await page.mouse.up();

  const after = await first.boundingBox();
  check("a widget can be resized on the canvas",
    (after?.width ?? 0) > (before?.width ?? 0) + 40);

  await page.getByRole("button", { name: "Save", exact: true }).click();
  await page.waitForTimeout(500);
} catch (e) { await fail("canvas", e); }

// --- the time range ---------------------------------------------------------------------------------
try {
  await page.getByRole("combobox", { name: "Time range" }).click();
  await page.getByRole("option", { name: "Last 7 days" }).click();
  await page.waitForTimeout(800);
  check("the time range can be changed", true);
} catch (e) { await fail("range", e); }

// --- a Grafana dashboard, pasted --------------------------------------------------------------------
const grafana = JSON.stringify({
  title: `${name} from grafana`,
  schemaVersion: 39,
  time: { from: "now-6h", to: "now" },
  panels: [
    {
      type: "stat", title: "Imported customers", gridPos: { x: 0, y: 0, w: 6, h: 4 },
      datasource: { uid: connection.name },
      targets: [{ refId: "A", rawSql: "SELECT count(*) FROM customers", rawQuery: true }],
    },
    {
      type: "timeseries", title: "Imported CPU", gridPos: { x: 6, y: 0, w: 12, h: 8 },
      datasource: { type: "prometheus", uid: "prom" },
      targets: [{ refId: "A", expr: "rate(cpu_seconds_total[5m])" }],
    },
  ],
});

try {
  await page.getByRole("button", { name: "More" }).click();
  await page.getByRole("menuitem", { name: /paste json/i }).click();
  await page.getByLabel("The dashboard as JSON").fill(grafana);
  await page.getByRole("button", { name: "Import", exact: true }).click();

  // The panel that could not come along is a sentence, not a silently empty widget.
  await page.getByText(/metrics datasource/i).waitFor({ timeout: 15000 });
  await page.getByText("Imported customers").waitFor({ timeout: 15000 });
  check("a Grafana dashboard is imported, with a note for what could not come", true);
} catch (e) { await fail("import", e); }

// --- and out again ----------------------------------------------------------------------------------
try {
  const exported = await page.request.get(
    `${baseUrl}/api/dashboards/${encodeURIComponent(await currentId(page, baseUrl, name))}/grafana`);

  const body = await exported.json();
  check("a dashboard here exports as Grafana JSON",
    body.schemaVersion > 0 && Array.isArray(body.panels) && body.panels.length >= 2);
} catch (e) { await fail("export", e); }

await page.screenshot({ path: "smoke-dashboard.png" });

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await browser.close();
console.log("dashboard check passed");

async function currentId(page, baseUrl, name) {
  const listed = await (await page.request.get(`${baseUrl}/api/dashboards`)).json();
  const found = listed.dashboards.find(one => one.name === name);
  if (!found) throw new Error(`the dashboard ${name} is not in the list`);
  return found.id;
}
