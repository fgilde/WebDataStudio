// Browser check for the MCP dialog in the header, and for the JSON-RPC endpoint behind it.
// Needs a server started with WDS_MCP_ENABLED=true (BASE_URL defaults to :5005).
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

// --- the endpoint itself ------------------------------------------------------------------------
const health = await (await page.request.get(`${baseUrl}/api/health`)).json();

if (!health.mcp) {
  console.log("skipped: this studio has no MCP endpoint (set WDS_MCP_ENABLED=true)");
  await browser.close();
  process.exit(0);
}

const rpc = async (method, params) => (await page.request.post(`${baseUrl}${health.mcp.path}`, {
  data: { jsonrpc: "2.0", id: 1, method, params },
})).json();

const initialize = await rpc("initialize");
check(`the handshake names the server (${initialize.result?.serverInfo?.name})`,
  initialize.result?.serverInfo?.name === "webdatastudio");

const tools = (await rpc("tools/list")).result.tools.map(t => t.name);
check(`the tools are offered (${tools.length})`,
  tools.includes("list_connections") && tools.includes("run_query"));
check("writing tools are absent while writing is off",
  health.mcp.writes || !tools.includes("apply_script"));

const connections = await rpc("tools/call", { name: "list_connections", arguments: {} });
check("an agent can list the connections",
  connections.result.isError === false && connections.result.content[0].text.includes("DEMO"));

// A write must be refused by run_query, whatever the flag says: that is what preview/apply are for.
const id = JSON.parse(connections.result.content[0].text)[0].id;
const write = await rpc("tools/call", {
  name: "run_query", arguments: { connectionId: id, sql: "DELETE FROM people" },
});
check("run_query refuses a write", write.result.isError === true);

// --- the dialog in the header -------------------------------------------------------------------
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByLabel("MCP").waitFor({ timeout: 20000 });
await page.getByLabel("MCP").click();
await page.getByText("This studio as an MCP server").waitFor({ timeout: 10000 });

const dialog = await page.locator(".mantine-Modal-content").innerText();
check("the dialog shows the endpoint URL", dialog.includes(`${health.mcp.path}`));
check("and the client configuration", dialog.includes("claude mcp add"));
check("and the tools it offers", dialog.includes("list_connections"));

await page.getByRole("tab", { name: "Claude Desktop" }).click();
await page.waitForTimeout(300);
check("the Claude Desktop snippet is JSON",
  (await page.locator(".mantine-Modal-content").innerText()).includes("mcpServers"));

await page.keyboard.press("Escape");

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));

await page.screenshot({ path: "smoke-mcp.png" });
await browser.close();
console.log("mcp smoke passed");
