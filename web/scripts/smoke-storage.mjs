// Browser check for object storage: the tree comes one page at a time, an object has its own facts
// and a preview, a CSV in a bucket browses like a table, and a file nothing reads says so.
//
// Needs a running server with a connection whose engine is "storage" — a folder is enough:
// WDS_CONN_LAKE=file:///tmp/wds/lake (see docs/guide/development.md). BASE_URL defaults to :5005.
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
const lake = connections.find(connection => connection.engine === "storage");

if (!lake) {
  console.log("skipped: this smoke needs a connection whose engine is storage");
  await browser.close();
  process.exit(0);
}

// --- the fixture, put there through the studio's own upload -------------------------------------
const upload = async (ref, name, body, type) => {
  const response = await page.request.post(
    `${baseUrl}/api/storage/${lake.id}/upload?ref=${encodeURIComponent(ref)}&name=${name}`,
    { headers: { "content-type": type }, data: body });

  check(`uploaded ${name}`, response.ok());
  return (await response.json()).key;
};

const container = (await (await page.request.get(`${baseUrl}/api/schema/${lake.id}`)).json())[0];

check("the root of the tree is a container", container.kind === "Container");

const containerRef = container.ref;
// Everything after the kind is the path. A local folder's "container" is a whole path — with a
// drive letter and its colon on Windows — so only the kind comes off, not everything up to the
// first colon.
const path = containerRef.slice("Container:".length);
const prefixRef = `Prefix:${path}/exports`;

await upload(containerRef, "readme.txt", "a file at the root\n", "text/plain");
await upload(prefixRef, "people.csv", "name,age\nada,36\ngrace,45\nalan,41\n", "text/csv");
await upload(prefixRef, "notes.zip", "PK not a table", "application/zip");

// --- the tree: prefixes and objects, nothing walked --------------------------------------------
const children = async (ref) =>
  await (await page.request.get(
    `${baseUrl}/api/schema/${lake.id}?parent=${encodeURIComponent(ref)}`)).json();

const top = await children(containerRef);

check("a prefix shows as a folder", top.some(n => n.kind === "Prefix" && n.label === "exports"));
check("an object shows with its size",
  top.some(n => n.kind === "StorageObject" && n.label === "readme.txt" && /B/.test(n.detail ?? "")));
check("nothing from a deeper level leaks into this one",
  !top.some(n => n.label === "people.csv"));

const inside = await children(prefixRef);
check("the folder holds what was put in it", inside.length === 2);

// --- an object: its facts, and what a reader makes of it ---------------------------------------
const objectRef = `StorageObject:${path}/exports/people.csv`;
const preview = await (await page.request.get(
  `${baseUrl}/api/storage/${lake.id}/preview?ref=${encodeURIComponent(objectRef)}`)).json();

check("the preview carries the text", preview.text?.startsWith("name,age"));
check("and says it is queryable", preview.queryable === true);
check("and what a query would select from", /read_csv_auto\('/.test(preview.from ?? ""));
check("and the provider's own URI", (preview.uri ?? "").length > 0);

const zipRef = `StorageObject:${path}/exports/notes.zip`;
const zip = await (await page.request.get(
  `${baseUrl}/api/storage/${lake.id}/preview?ref=${encodeURIComponent(zipRef)}`)).json();

check("something no reader understands is not offered as a table", zip.queryable === false);

const refused = await page.request.get(`${baseUrl}/api/data/${lake.id}?ref=${encodeURIComponent(zipRef)}`);
check("and browsing it is refused with a reason rather than failing SQL", refused.status() === 400);

// --- the file as a table, through the endpoint the data tab uses --------------------------------
const rows = async (query) =>
  (await (await page.request.get(
    `${baseUrl}/api/data/${lake.id}?ref=${encodeURIComponent(objectRef)}&${query}`)).json()).rows;

check("a CSV in a bucket sorts", (await rows("sort=age&desc=true"))[0][0] === "grace");
check("and filters with the studio's own filter language",
  (await rows(`filterColumn=age&filter=${encodeURIComponent(">40")}`)).length === 2);
check("and pages", (await rows("sort=name&limit=1&offset=1")).length === 1);

// --- the probe behind the wizard's Test button --------------------------------------------------
const probe = async (url) => await (await page.request.post(`${baseUrl}/api/connections/test`, {
  data: { name: "PROBE", engine: "storage", connectionString: url, readOnly: false },
})).json();

const reached = await probe(`file:///${path.replace(/^\/+/, "")}`);
check("a storage connection is tested by reaching it", reached.ok === true);
check("and the answer says what is in there", /object\(s\)/.test(reached.message));

const missing = await probe("s3://no-such-bucket-wds?region=eu-central-1");
check("a bucket that is not there is not a green tick", missing.ok === false);

// --- the UI: the tree, the preview panel, the rows ---------------------------------------------
await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.goto(baseUrl, { waitUntil: "networkidle" });

await page.getByText(lake.name, { exact: true }).first().click();
await page.getByText(container.label, { exact: true }).first().click();
await page.getByText("exports", { exact: true }).first().click();

await page.getByText("people.csv", { exact: true }).first().click();

// The panel fetches the preview after the click, so the text of the file is what to wait for.
await page.getByText("name,age").first().waitFor({ timeout: 20000 });

check("the panel shows the object's content", await page.getByText("ada,36").first().isVisible());
check("and the columns it would have as a table",
  await page.getByText("VARCHAR").first().isVisible());

await page.getByText("people.csv", { exact: true }).first().dblclick();
await page.getByText("grace", { exact: true }).first().waitFor({ timeout: 20000 });

check("and a double-click opens its rows", await page.getByText("grace").first().isVisible());

// --- what a folder needs before it is one table ------------------------------------------------
const folderRows = await page.request.get(
  `${baseUrl}/api/data/${lake.id}?ref=${encodeURIComponent(prefixRef)}`);
check("a folder without a pattern is not a table", folderRows.status() === 400);

const globRef = `${prefixRef}/*.csv`;
const glob = await page.request.get(
  `${baseUrl}/api/data/${lake.id}?ref=${encodeURIComponent(globRef)}`);
check("with one it is", glob.ok());

// --- and the cleanup, which is the delete path ------------------------------------------------
for (const ref of [objectRef, zipRef, `StorageObject:${path}/readme.txt`]) {
  const response = await page.request.delete(
    `${baseUrl}/api/storage/${lake.id}?ref=${encodeURIComponent(ref)}`);
  check(`deleted ${ref.split("/").pop()}`, response.ok());
}

check("no console errors", errors.length === 0);
if (errors.length) console.log(errors.join("\n"));

await browser.close();
console.log("storage smoke ok");
