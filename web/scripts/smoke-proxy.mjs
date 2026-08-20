// Browser smoke test through a reverse proxy that decodes %2F, which is what sits in front of a
// deployed studio: Envoy on Azure Container Apps normalises the path before routing, so an object
// reference in a path segment ("Table:dbo/AbpUsers") arrives split in two and matches no route.
// Every object lookup answered 404 in the cloud while being fine locally — this reproduces that
// environment on a laptop.
//
// Point BASE_URL at a running studio (default http://localhost:5005); the proxy in front of it is
// started by this script.
import { chromium } from "playwright";
import { createServer } from "node:http";
import { request } from "node:http";

const target = new URL(process.env.BASE_URL ?? "http://localhost:5005");
const port = Number(process.env.PROXY_PORT ?? 5199);
const failures = [];

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) failures.push(label);
};

// The proxy: decode the path exactly the way a normalising proxy does, then pass it on.
const proxy = createServer((incoming, outgoing) => {
  const [path, query] = incoming.url.split("?");
  const decoded = decodeURIComponent(path);

  const forwarded = request({
    host: target.hostname,
    port: target.port,
    method: incoming.method,
    path: decoded + (query ? `?${query}` : ""),
    headers: { ...incoming.headers, host: target.host },
  }, response => {
    outgoing.writeHead(response.statusCode ?? 502, response.headers);
    response.pipe(outgoing);
  });

  forwarded.on("error", error => {
    outgoing.writeHead(502);
    outgoing.end(String(error));
  });

  incoming.pipe(forwarded);
});

await new Promise(resolve => proxy.listen(port, resolve));
const baseUrl = `http://localhost:${port}`;
console.log(`proxy on ${baseUrl} → ${target.origin}, decoding %2F like Envoy does`);

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

page.on("pageerror", e => failures.push(`pageerror: ${e}`));
page.on("response", r => {
  if (r.url().includes("/api/") && r.status() >= 400) failures.push(`${r.status()} ${r.url()}`);
});

await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).click();
await page.getByText("Tables", { exact: true }).click();

const explorer = page.locator(".dv-groupview").filter({ has: page.getByPlaceholder("Filter") }).first();
const people = explorer.getByText("people", { exact: true }).first();
await people.waitFor({ timeout: 20000 });

// Structure — the object description.
await page.getByRole("tab", { name: "Structure" }).click();
await people.click();
await page.getByRole("tab", { name: "Columns" }).waitFor({ timeout: 15000 });
check("the structure panel describes the table", await page.getByRole("tab", { name: "Columns" }).count() === 1);

// Double click — the rows.
await people.dblclick();
await page.getByText("london", { exact: true }).first().waitFor({ timeout: 20000 });
check("a double click shows the rows", true);

// Right click → Indexes — the DDL.
await people.click({ button: "right" });
await page.getByText("Indexes…").first().click();
await page.getByText(/^Indexes of/).waitFor({ timeout: 15000 });
check("the index dialog loads the DDL", true);
await page.keyboard.press("Escape");

check("no API call failed behind the proxy", failures.filter(f => /^\d/.test(f)).length === 0);

await browser.close();
proxy.close();

if (failures.length > 0) {
  console.error(failures.slice(0, 5).join(" | "));
  process.exit(1);
}
console.log("proxy smoke passed");
