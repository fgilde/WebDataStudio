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

// --- switching to another dashboard --------------------------------------------------------------
// GridStack only adopts the elements that exist when it starts, so the widgets of the *next*
// dashboard were once elements it had never seen: unmanaged, unpositioned, all piled into the
// corner at the same size. This is that bug, as a check.
try {
  await page.request.post(`${baseUrl}/api/dashboards`, {
    data: {
      name: `${name} second`,
      widgets: [
        {
          id: "s1", type: "Stat", title: "Second dashboard",
          position: { x: 0, y: 0, w: 8, h: 5 },
          source: { kind: "Sql", connectionId: connection.id, sql: "SELECT count(*) AS n FROM customers" },
          mapping: {}, options: { legend: true, thresholds: [] },
        },
        {
          id: "s2", type: "Table", title: "Its rows",
          position: { x: 8, y: 0, w: 16, h: 5 },
          source: { kind: "Sql", connectionId: connection.id, sql: "SELECT * FROM customers LIMIT 5" },
          mapping: {}, options: { legend: true, thresholds: [] },
        },
      ],
    },
  });

  const boxes = () => page.locator(".grid-stack-item").evaluateAll(items => items.map(one => {
    const { x, width, height } = one.getBoundingClientRect();
    return { x: Math.round(x), width: Math.round(width), height: Math.round(height) };
  }));

  // Saved through the API, so this browser has not seen it yet — which is what the refresh button
  // is for.
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await page.waitForTimeout(1200);

  const open = async (label) => {
    await page.getByRole("combobox", { name: "Dashboard" }).click();
    await page.getByRole("option", { name: label, exact: true }).click();
    await page.waitForTimeout(1500);
  };

  await open(`${name} second`);

  const second = await boxes();
  if (second.length !== 2) throw new Error(`the second dashboard drew ${second.length} widgets`);
  // Side by side and each wider than the corner they used to collapse into.
  if (second[0].x === second[1].x)
    throw new Error("both widgets start at the same column");
  if (second.some(one => one.width < 100 || one.height < 100))
    throw new Error(`a widget came out at ${JSON.stringify(second)}`);

  check("switching to another dashboard positions its widgets", true);

  // And back: the first dashboard's own widgets are managed again rather than left behind.
  await open(name);

  const first = await boxes();
  if (first.length !== 2) throw new Error(`the first dashboard drew ${first.length} widgets`);
  if (first.some(one => one.width < 100 || one.height < 100))
    throw new Error(`a widget came back at ${JSON.stringify(first)}`);

  check("and switching back positions them again", true);
} catch (e) { await fail("switch", e); }

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
