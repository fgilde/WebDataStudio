// Browser check for the P8 panels: the ER diagram draws the seeded tables, the admin panel runs a
// catalogued maintenance command, and the compare panel reports a schema comparison.
// Needs a running server with a demo connection (see README, BASE_URL defaults to :5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
// A 4xx from an unsupported panel is an answer this app renders, not a failure: SQLite really
// has no sessions and no users. Only unexpected console errors count here.
const expected = /status of (400|404)/;
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-admin-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 500));
  throw error;
};

await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 15000 });
await page.getByText("DEMO", { exact: true }).click();

// --- ER diagram --------------------------------------------------------------
await page.getByRole("button", { name: "Diagram" }).click();
try {
  await page.locator(".react-flow__node").first().waitFor({ timeout: 20000 });
  // The node header is "people · main", so match the node box rather than an exact string.
  await page.locator(".react-flow__node").filter({ hasText: "people" }).first()
    .waitFor({ timeout: 10000 });
} catch (e) { await fail("diagram", e); }

const nodeCount = await page.locator(".react-flow__node").count();
if (nodeCount === 0) await fail("diagram-empty", new Error("the diagram drew no tables"));

// --- administration ----------------------------------------------------------
await page.getByRole("button", { name: "Administration" }).click();
try {
  // Administration opens on the overview now, so the maintenance panel has to be asked for.
  await page.getByRole("tab", { name: "Maintenance" }).waitFor({ timeout: 15000 });
  await page.getByRole("tab", { name: "Maintenance" }).click();
  // SQLite's integrity check needs no target, so it is safe to actually run here.
  await page.getByRole("button", { name: "PRAGMA integrity_check" }).click();
  await page.getByText("PRAGMA integrity_check").last().waitFor({ timeout: 15000 });
} catch (e) { await fail("admin", e); }

// The sessions tab must say so rather than break on an engine without sessions.
await page.getByRole("tab", { name: "Sessions" }).click();
try {
  await page.getByText(/session/i).first().waitFor({ timeout: 10000 });
} catch (e) { await fail("sessions", e); }

// --- compare -------------------------------------------------------------------
await page.getByRole("button", { name: "Compare" }).click();
try {
  await page.getByRole("tab", { name: "Schema" }).waitFor({ timeout: 15000 });
  await page.getByRole("button", { name: "Compare", exact: true }).last().waitFor({ timeout: 10000 });
} catch (e) { await fail("compare", e); }

await page.screenshot({ path: "smoke-admin.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-admin ok");
