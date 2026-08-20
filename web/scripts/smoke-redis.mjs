// Browser check for the Redis panel: browse the keyspace, edit a value through the preview, set a
// TTL, delete by pattern, and read the analysis. Needs a server with a Redis connection —
// WDS_CONN_CACHE=redis://localhost:6399 or similar. Without one the script says so and stops,
// rather than failing as if something were broken.
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
const redis = connections.find(connection => connection.engine === "redis");

if (!redis) {
  console.log("skipped: this studio has no Redis connection (set WDS_CONN_<NAME>=redis://…)");
  await browser.close();
  process.exit(0);
}

// A key of this run's own, so the smoke is repeatable and cannot delete somebody's data.
const stamp = Date.now().toString(36);
const key = `smoke:${stamp}:greeting`;
const doomed = [`smoke-doomed:${stamp}:1`, `smoke-doomed:${stamp}:2`];

await page.request.post(`${baseUrl}/api/query/execute`, {
  data: { connectionId: redis.id, sql: [`SET ${key} hello`, ...doomed.map(k => `SET ${k} x`)].join("\n") },
});

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText(redis.name, { exact: true }).first().click();
await page.waitForTimeout(400);

// The browser button only exists for a Redis connection.
await page.getByRole("button", { name: "Redis browser" }).click();
await page.getByRole("tab", { name: "Keys" }).waitFor({ timeout: 20000 });

const panel = page.locator(".dv-groupview").filter({ has: page.getByRole("tab", { name: "Analysis" }) }).first();

// --- browsing ---------------------------------------------------------------------------------
await panel.getByPlaceholder("user:*").fill(`smoke:${stamp}:*`);
await page.waitForTimeout(1200);
check("the pattern finds the key", await panel.getByText(key, { exact: true }).count() === 1);

await panel.getByText(key, { exact: true }).click();
await page.waitForTimeout(800);
check("the value opens with its type", (await panel.innerText()).includes("string"));

// --- editing through the preview ---------------------------------------------------------------
const editor = panel.locator("textarea").first();
await editor.fill("changed by the smoke");
await panel.getByRole("button", { name: "Save", exact: true }).click();
await page.getByText("Apply this change?").waitFor({ timeout: 10000 });
check("the preview shows the command", (await page.locator(".mantine-Modal-content").innerText()).includes("SET"));
await page.getByRole("button", { name: "Run it" }).click();
await page.waitForTimeout(1200);

const after = await (await page.request.get(
  `${baseUrl}/api/redis/${redis.id}/value?key=${encodeURIComponent(key)}`)).json();
check(`the value was written (${after.value})`, after.value === "changed by the smoke");

// --- a TTL ------------------------------------------------------------------------------------
await panel.getByPlaceholder("TTL seconds").fill("600");
await panel.getByPlaceholder("TTL seconds").press("Enter");
await page.getByText("Apply this change?").waitFor({ timeout: 10000 });
await page.getByRole("button", { name: "Run it" }).click();
await page.waitForTimeout(1000);

const withTtl = await (await page.request.get(
  `${baseUrl}/api/redis/${redis.id}/value?key=${encodeURIComponent(key)}`)).json();
check(`the ttl is set (${withTtl.ttlSeconds}s)`, withTtl.ttlSeconds > 0 && withTtl.ttlSeconds <= 600);

// --- delete by pattern, with the preview first --------------------------------------------------
await panel.getByPlaceholder("user:*").fill(`smoke-doomed:${stamp}:*`);
await page.waitForTimeout(1200);
await panel.getByRole("button", { name: "Delete matching…" }).click();
await page.getByText(/Delete \d+ keys\?/).waitFor({ timeout: 10000 });
check("the bulk preview counts what matched",
  (await page.locator(".mantine-Modal-content").innerText()).includes(doomed[0]));
await page.getByRole("button", { name: "Run it" }).click();
await page.waitForTimeout(1200);

const remaining = await (await page.request.get(
  `${baseUrl}/api/redis/${redis.id}/keys?match=smoke-doomed:${stamp}:*`)).json();
check("the matching keys are gone", remaining.keys.length === 0);

const survivor = await (await page.request.get(
  `${baseUrl}/api/redis/${redis.id}/keys?match=${encodeURIComponent(key)}`)).json();
check("and the key that did not match survived", survivor.keys.length === 1);

// --- the analysis -----------------------------------------------------------------------------
await panel.getByRole("tab", { name: "Analysis" }).click();
await panel.getByText("Memory by prefix").waitFor({ timeout: 20000 });
const analysis = await panel.innerText();
check("the analysis reports prefixes and memory",
  analysis.includes("Memory by prefix") && /\d+ keys/.test(analysis) && analysis.includes("Largest keys"));

// Clean up after the run.
await page.request.post(`${baseUrl}/api/query/execute`, {
  data: { connectionId: redis.id, sql: `DEL ${key}` },
});

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-redis.png" });
await browser.close();
console.log("redis smoke passed");
