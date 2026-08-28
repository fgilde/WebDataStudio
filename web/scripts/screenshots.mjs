// Captures the screenshots the documentation and the site use, in a dark and a light theme.
//
// Needs a running server with the demo connections seeded from scripts/demo-data (see
// docs/guide/development.md). BASE_URL defaults to :5005. The shots of what only PostgreSQL has —
// policies, partitions, a function's trial run, the dashboard graphs — are taken when a PostgreSQL
// connection is there and skipped when it is not, so the script still finishes on SQLite alone.
import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const out = process.env.OUT ?? "../docs/assets/screenshots";
await mkdir(out, { recursive: true });

const browser = await chromium.launch();

// Whether this studio has a server-based connection: the shots that need one say so and are
// skipped otherwise.
const probe = await browser.newContext();
const connections = await probe.request.get(`${baseUrl}/api/connections`)
  .then(r => r.json()).catch(() => []);
await probe.close();

const postgres = connections.find(connection => connection.engine === "postgresql");
const lake = connections.find(connection => connection.engine === "storage");
const demo = connections.find(connection => connection.name === "DEMO") ?? connections[0];

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

  // The tools live in one menu now: the explorer's icon row was cut off at any sensible width.
  const tool = async (label) => {
    await page.getByRole("banner").getByRole("button", { name: "Tools" }).click();
    await page.getByRole("menuitem", { name: label }).first().click();
  };

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
  await page.keyboard.insertText("SELECT p.id, p.name, p.city, count(o.id) AS orders\n"
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
  await tool(/ER diagram/);
  await page.locator(".react-flow__node").first().waitFor({ timeout: 25000 });
  await page.waitForTimeout(700);
  await shot("diagram");

  // --- administration ---------------------------------------------------------------
  await tool(/^Administration/);
  await page.getByRole("tab", { name: "Maintenance" }).waitFor({ timeout: 20000 });
  await shot("admin");

  // --- compare ------------------------------------------------------------------------
  await tool(/Compare two connections/);
  await page.getByRole("tab", { name: "Schema" }).waitFor({ timeout: 20000 });
  await shot("compare");

  // --- query builder ----------------------------------------------------------------------
  await tool(/Query builder/);
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

  // --- the data tab: the filter language and the values a column holds -----------------------
  // Six panels deep the grid would be a sliver, and a shot of a sliver teaches nothing. Back to the
  // default arrangement, which is also what a reader of the docs is looking at.
  await page.keyboard.press("Control+l");
  await page.keyboard.press("0");
  await page.waitForTimeout(1500);

  await page.getByText("DEMO", { exact: true }).first().click();
  const tables = page.locator(".mantine-UnstyledButton-root").filter({ hasText: /^Tables$/ }).first();
  await tables.waitFor({ timeout: 20000 });
  await tables.click();

  const orders = page.locator(".mantine-UnstyledButton-root").filter({ hasText: /^orders$/ }).first();
  await orders.waitFor({ timeout: 20000 });
  await orders.dblclick();
  await page.getByRole("button", { name: "Insert row" }).waitFor({ timeout: 20000 });

  const dataPanel = page.locator(".dv-groupview")
    .filter({ has: page.getByRole("button", { name: "Insert row" }) }).first();

  // The column menu holds both: the filter box with its syntax, and the values with their counts.
  await dataPanel.locator("thead").getByText("status", { exact: true }).first().click();
  await page.getByText("hover for all", { exact: false }).first().waitFor({ timeout: 15000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${out}/filter-${suffix}.png` });
  await page.keyboard.press("Escape");

  // A column borrowed from the table the key points at.
  await dataPanel.locator("thead").getByText("person_id", { exact: true }).first().click();
  await page.getByText("from people").waitFor({ timeout: 15000 });
  await page.getByRole("menuitem", { name: "name" }).first().click();
  await page.getByText("borrowed").waitFor({ timeout: 15000 });
  await shot("borrowed");

  // --- what is inside a JSON column -----------------------------------------------------------
  {
    const events = page.locator(".mantine-UnstyledButton-root")
      .filter({ hasText: /^events$/ }).first();

    // Waited for rather than counted: the tree renders its nodes a moment after the folder opens,
    // and a count taken too early reads zero for a node that is on its way.
    const hasEvents = await events.waitFor({ timeout: 8000 }).then(() => true, () => false);

    if (hasEvents) {
      await events.dblclick();
      await page.waitForTimeout(1500);

      const panel = page.locator(".dv-groupview")
        .filter({ has: page.getByRole("button", { name: "Insert row" }) }).first();

      await panel.locator("thead").getByText("payload", { exact: true }).first().click();
      const ask = page.getByText("What is in this JSON", { exact: false }).first();
      const asked = await ask.waitFor({ timeout: 8000 }).then(() => true, () => false);

      if (asked) {
        await ask.click();
        // The report reads a sample of the documents, so the shot waits for the paths.
        await page.getByText("plan", { exact: true }).first().waitFor({ timeout: 20000 });
        await page.waitForTimeout(600);
        await shot("json-shape");
        await page.keyboard.press("Escape");
      } else {
        await page.keyboard.press("Escape");
        console.log("skipped the JSON shot: no menu entry for this column");
      }
    } else {
      console.log("skipped the JSON shot: this demo data has no events table");
    }
  }

  // --- rules about the data ---------------------------------------------------------------------
  {
    // Two rules through the API the panel calls, so the shot has something to show; they are the
    // same call the panel makes when somebody presses Add rule.
    const rule = (body) => page.request.put(`${baseUrl}/api/quality/${demo?.id}`, { data: body });

    await rule({
      id: "shot-city", connectionId: demo?.id, schema: "", table: "people", column: "city",
      kind: "NotNull", argument: null, message: "every person needs a city", enabled: true,
    });
    await rule({
      id: "shot-total", connectionId: demo?.id, schema: "", table: "orders", column: "total",
      kind: "Range", argument: "0..1000", message: null, enabled: true,
    });

    await tool(/^Administration/);
    await page.getByRole("tab", { name: "Data quality" }).first().click();
    await page.getByText("every person needs a city").first().waitFor({ timeout: 20000 });
    await page.getByRole("button", { name: "Run now" }).click();
    await page.getByText(/ok|rows/).first().waitFor({ timeout: 20000 });
    await page.waitForTimeout(600);
    await shot("quality");

    // And the trail, which by now has this run in it.
    await page.getByRole("tab", { name: "Audit" }).first().click();
    await page.waitForTimeout(1200);
    await shot("audit");

    for (const id of ["shot-city", "shot-total"])
      await page.request.delete(`${baseUrl}/api/quality/${demo?.id}/${id}`);
  }

  // --- perspective ----------------------------------------------------------------------------
  await tool(/Perspective/);
  const start = page.getByPlaceholder("Start from");
  await start.waitFor({ timeout: 20000 });
  await start.click();
  await page.getByRole("option", { name: /people$/ }).first().click();

  const firstRow = page.getByText("name=Ada Lovelace", { exact: false }).first();
  await firstRow.waitFor({ timeout: 20000 });
  await firstRow.click();
  const branch = page.getByText("orders (person_id)").first();
  await branch.waitFor({ timeout: 15000 });
  await branch.click();
  await page.getByText("total=", { exact: false }).first().waitFor({ timeout: 20000 });
  await shot("perspective");

  // --- the map ---------------------------------------------------------------------------------
  // Back to the demo connection first: a new tab opens on whatever is selected, and `places` is
  // only in this one.
  await page.getByText("DEMO", { exact: true }).first().click();
  await page.waitForTimeout(500);

  await page.getByRole("button", { name: "New query" }).click();
  // Only the active tab's editor is visible; the ones behind it are still in the DOM.
  const editor = page.locator(".monaco-editor:visible").first();
  await editor.waitFor({ timeout: 20000 });
  await editor.click();
  await page.keyboard.insertText("SELECT name, lat, lon FROM places ORDER BY name");
  await page.keyboard.press("F5");
  await page.getByText("Reykjavik", { exact: true }).first().waitFor({ timeout: 20000 });
  await page.getByText("Map", { exact: true }).first().click();
  await page.getByRole("img", { name: "the result's geography" }).waitFor({ timeout: 15000 });
  await shot("map");

  // --- archives ---------------------------------------------------------------------------------
  const kept = await page.request.post(`${baseUrl}/api/archives/people-before-the-migration`, {
    data: { connectionId: demo?.id, sql: "SELECT id, name, city, signed_up FROM people" },
  });

  // A shot of an empty panel would be worse than no shot: say why instead.
  if (!kept.ok()) throw new Error(`could not keep an archive: ${await kept.text()}`);

  await tool(/Archives/);
  const archive = page.getByText("people-before-the-migration").first();
  await archive.waitFor({ timeout: 20000 });
  await archive.click();
  await page.getByText("rows", { exact: false }).first().waitFor({ timeout: 20000 });
  await shot("archives");

  // --- preferences ------------------------------------------------------------------------------
  await page.keyboard.press("Control+Comma");
  await page.getByText("Rows per page in the data tab").waitFor({ timeout: 15000 });
  await page.getByRole("tab", { name: "Keyboard" }).click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${out}/preferences-${suffix}.png` });
  await page.keyboard.press("Escape");

  // --- what only PostgreSQL has ------------------------------------------------------------------
  if (postgres) {
    // Back to the default arrangement: by now nine panels are open and everything is a narrow
    // column, which is not what any of this looks like when it is used.
    await page.keyboard.press("Control+l");
    await page.keyboard.press("0");
    await page.waitForTimeout(1500);

    // The structure tabs hold tables of their own; maximised, they are readable in a shot.
    const maximize = async () => {
      await page.getByRole("tab", { name: "Structure" }).click({ button: "right" });
      await page.getByText("Maximize", { exact: true }).click();
      await page.waitForTimeout(600);
    };

    const restore = async () => {
      await page.getByRole("tab", { name: "Structure" }).click({ button: "right" });
      await page.getByText("Restore", { exact: true }).click();
      await page.waitForTimeout(600);
    };

    await page.getByText(postgres.name, { exact: true }).first().click();
    await page.getByText("public", { exact: true }).first().click();
    await page.getByText("Tables", { exact: true }).first().click();

    // Row-level security and its policies.
    await page.getByText("tenants", { exact: true }).first().click();
    await page.getByRole("tab", { name: "Policies" }).click();
    await page.getByText("row security on").waitFor({ timeout: 20000 });
    await maximize();
    await shot("policies");
    await restore();

    // The partitions of a partitioned table.
    await page.getByText("events", { exact: true }).first().click();
    await page.getByRole("tab", { name: "Partitions" }).click();
    await page.getByText("RANGE", { exact: true }).waitFor({ timeout: 20000 });
    await maximize();
    await shot("partitions");
    await restore();

    // A function, and what its run raised.
    await page.getByText("Functions", { exact: true }).first().click();
    await page.getByText("spent_by", { exact: true }).first().click();
    await page.getByRole("tab", { name: "Inspect" }).click();
    await page.getByText("plpgsql", { exact: true }).waitFor({ timeout: 20000 });
    // Run first, then maximise: maximising re-lays out the panel, and a click that lands while it
    // moves lands nowhere. The result stays on screen either way.
    const runIt = page.getByRole("button", { name: "Run and roll back" });
    await runIt.waitFor({ timeout: 20000 });
    await runIt.click();

    // The argument is left empty on purpose: the function's own default applies, and the notice says
    // which one it used. Waiting for the heading rather than the text: the same phrase is in the
    // source right below it, and two matches are not a wait.
    await page.getByText("Raised", { exact: true }).waitFor({ timeout: 25000 });
    await maximize();
    await shot("inspect");
    await restore();

    // The dashboard, which needs a server to have numbers about. Back to the default arrangement
    // first: the structure panel from the shots above is not part of this one.
    await page.keyboard.press("Control+l");
    await page.keyboard.press("0");
    await page.waitForTimeout(1500);
    await page.getByText(postgres.name, { exact: true }).first().click();

    await tool(/^Administration/);
    await page.getByRole("tab", { name: "Overview" }).click();
    await page.getByText("Over time").waitFor({ timeout: 25000 });

    // A line needs more than one reading, and an idle server draws a flat one. The tiles poll every
    // five seconds; this keeps the server busy across four of them.
    for (let round = 0; round < 8; round++) {
      // Not awaited, and deliberately slow: a query that finishes in a millisecond is never
      // running when the poll looks, and the lines stay flat.
      for (let n = 0; n < 3; n++)
        void page.request.post(`${baseUrl}/api/query/execute`, {
          data: {
            connectionId: postgres.id,
            sql: "SELECT pg_sleep(2), count(*) FROM events e JOIN customers c ON true",
            maxRows: 1,
          },
        }).catch(() => {});

      await page.waitForTimeout(2600);
    }

    await shot("dashboard");
  } else {
    console.log("skipped the PostgreSQL shots: no such connection");
  }

  // --- finding a value rather than a table ----------------------------------------------------
  // A clean layout: the panels from the shots above would fill the frame around this one.
  await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
  await page.goto(baseUrl, { waitUntil: "networkidle" });

  // The tool opens on the active connection, and after a reload that is whichever came first: the
  // demo database is the one with tables to find something in.
  await page.getByText("DEMO", { exact: true }).click();
  await page.getByLabel("Find data").click();
  await page.getByLabel("Find this value").fill("london");
  await page.getByRole("button", { name: "Search" }).click();
  await page.getByText(/tables searched/).waitFor({ timeout: 30000 });
  await shot("datasearch");

  // --- object storage: the tree, the preview, a file as a table ------------------------------
  if (lake) {
    // The panels from the shots above would fill the middle of this one; a clean layout puts the
    // tree, the object and its rows in the frame instead.
    await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
    await page.goto(baseUrl, { waitUntil: "networkidle" });

    const container = (await (await page.request.get(`${baseUrl}/api/schema/${lake.id}`)).json())[0];
    const path = container.ref.slice("Container:".length);

    // Put something there through the studio's own upload, so the shot has content whichever
    // provider this connection points at.
    const upload = (ref, name, body, type) => page.request.post(
      `${baseUrl}/api/storage/${lake.id}/upload?ref=${encodeURIComponent(ref)}&name=${name}`,
      { headers: { "content-type": type }, data: body });

    await upload(`Prefix:${path}/exports`, "people.csv",
      "name,city,orders\nada,london,7\ngrace,new york,4\nalan,manchester,9\n",
      "text/csv");
    await upload(`Prefix:${path}/exports`, "readme.txt",
      "what lands here, and when\n", "text/plain");

    await page.getByText(lake.name, { exact: true }).first().click();
    await page.getByText(container.label, { exact: true }).first().click();
    await page.getByText("exports", { exact: true }).first().click();
    await page.getByText("people.csv", { exact: true }).first().click();
    await page.getByText("name,city,orders").first().waitFor({ timeout: 20000 });
    await shot("storage");

    await page.getByText("people.csv", { exact: true }).first().dblclick();
    await page.getByText("manchester", { exact: true }).first().waitFor({ timeout: 20000 });
    await shot("storage-query");

    for (const name of ["people.csv", "readme.txt"])
      await page.request.delete(
        `${baseUrl}/api/storage/${lake.id}?ref=${encodeURIComponent(`StorageObject:${path}/exports/${name}`)}`);
  } else {
    console.log("skipped the storage shots: no connection whose engine is storage");
  }

  // --- adding a bucket without writing a URL --------------------------------------------------
  await page.goto(`${baseUrl}/connections`, { waitUntil: "networkidle" });
  await page.getByRole("button", { name: "Add a bucket" }).click();
  // The wizard is a modal in a portal: wait for it rather than for the click to have "worked".
  await page.getByText(/anything else speaking S3/).waitFor({ timeout: 20000 });
  await page.getByLabel("Bucket", { exact: true }).fill("data-lake");
  await page.getByLabel("Prefix").fill("exports/2026");
  await page.getByLabel("Region").fill("eu-central-1");
  // The sign-in choice stays on the machine's own role: that is the one to show, and a shot with a
  // key field in it invites somebody to fill it in.
  await page.waitForTimeout(300);
  await page.screenshot({ path: `${out}/bucket-wizard-${suffix}.png` });
  await page.getByRole("button", { name: "Cancel" }).click();
  await page.goto(baseUrl, { waitUntil: "networkidle" });

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
