// Captures the screenshots the documentation and the site use, in a dark and a light theme.
// Needs a running server with a demo connection (BASE_URL defaults to :5005).
import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const out = process.env.OUT ?? "../docs/assets/screenshots";
await mkdir(out, { recursive: true });

const browser = await chromium.launch();

// The theme id is what the app stores; these two are the light and dark ends of the set.
for (const [theme, suffix] of [["ocean", "dark"], ["github-light", "light"]]) {
  const context = await browser.newContext({ viewport: { width: 1600, height: 950 } });
  const page = await context.newPage();

  await page.addInitScript(id => localStorage.setItem("webdatastudio.theme", id), theme);

  // Leftover tabs from another run would show up in every shot.
  await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
  await page.goto(baseUrl, { waitUntil: "networkidle" });
  await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 20000 });
  await page.getByText("DEMO", { exact: true }).click();

  // Park the pointer in the middle so no tooltip hangs over the shot.
  const shot = async (name) => {
    await page.mouse.move(800, 480);
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${out}/${name}-${suffix}.png` });
  };

  // --- query editor and result grid ------------------------------------------
  await page.getByRole("button", { name: "New query" }).click();
  await page.locator(".monaco-editor").first().waitFor({ timeout: 20000 });
  await page.locator(".monaco-editor").first().click();
  await page.keyboard.type("SELECT p.id, p.name, p.city, count(o.id) AS orders\n"
    + "  FROM people p\n  LEFT JOIN orders o ON o.person_id = p.id\n"
    + " GROUP BY p.id, p.name, p.city\n ORDER BY orders DESC");
  await page.keyboard.press("F5");
  await page.getByText("london", { exact: true }).first().waitFor({ timeout: 20000 });
  await shot("query");

  // --- chart ------------------------------------------------------------------
  await page.getByText("Chart", { exact: true }).click();
  await page.locator("svg[role='img']").first().waitFor({ timeout: 15000 });
  await shot("chart");
  await page.getByText("Grid", { exact: true }).click();

  // --- diagram ------------------------------------------------------------------
  await page.getByRole("button", { name: "Diagram" }).click();
  await page.locator(".react-flow__node").first().waitFor({ timeout: 25000 });
  await page.waitForTimeout(700);
  await shot("diagram");

  // --- administration ---------------------------------------------------------------
  await page.getByRole("button", { name: "Administration" }).click();
  await page.getByRole("tab", { name: "Maintenance" }).waitFor({ timeout: 20000 });
  await shot("admin");

  // --- compare ------------------------------------------------------------------------
  await page.getByRole("button", { name: "Compare" }).click();
  await page.getByRole("tab", { name: "Schema" }).waitFor({ timeout: 20000 });
  await shot("compare");

  // --- query builder ----------------------------------------------------------------------
  await page.getByRole("button", { name: "Query builder" }).click();
  const addTable = page.getByPlaceholder("Add a table");
  await addTable.waitFor({ timeout: 20000 });

  for (const table of ["people", "orders"]) {
    await addTable.click();
    await page.getByRole("option", { name: table, exact: true }).click();
    await page.waitForTimeout(700);
  }

  // A column on each card, so the shot shows a query rather than two empty tables.
  await page.locator(".react-flow__node").first().getByText("name", { exact: true }).click();
  await page.locator(".react-flow__node").nth(1).getByText("total", { exact: true }).click();
  await page.waitForTimeout(1400);
  await shot("builder");

  // --- table designer --------------------------------------------------------------------
  const tableNode = page.locator(".mantine-UnstyledButton-root").filter({ hasText: /^Tables$/ }).first();
  await tableNode.click();
  const people = page.locator(".mantine-UnstyledButton-root").filter({ hasText: /^people$/ }).first();
  await people.waitFor({ timeout: 20000 });
  await people.click({ button: "right" });
  await page.getByText("Design table…").click();
  await page.getByText(/columns?/i).first().waitFor({ timeout: 20000 });
  await shot("designer");

  // --- command palette -----------------------------------------------------------------------
  await page.keyboard.press("Control+k");
  await page.getByPlaceholder("Type a command").waitFor({ timeout: 10000 });
  await shot("palette");
  await page.keyboard.press("Escape");

  await context.close();
  console.log(`captured ${suffix}`);
}

await browser.close();
console.log("screenshots ok");
