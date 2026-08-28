// Browser check for the login screen, the studio title and the brand links.
// Needs a server started with WDS_USER, WDS_PASSWORD and WDS_TITLE:
//
//   WDS_USER=admin WDS_PASSWORD=secret WDS_TITLE="analytics studio" ./WebDataStudio.Server
//
// BASE_URL defaults to :5006, so it does not collide with the anonymous server the other
// smoke checks use.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5006";
const user = process.env.WDS_USER ?? "admin";
const password = process.env.WDS_PASSWORD ?? "secret";
const title = process.env.WDS_TITLE ?? "analytics studio";

const errors = [];
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1400, height: 950 } });
page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-login-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  throw error;
};

await page.goto(baseUrl, { waitUntil: "networkidle" });

// --- the login screen ------------------------------------------------------------
try {
  await page.getByRole("button", { name: "Sign in" }).waitFor({ timeout: 15000 });

  // The icon is the point of this screen; a broken source would render at zero width.
  const icon = page.getByAltText("WebDataStudio").first();
  await icon.waitFor({ timeout: 5000 });
  const box = await icon.boundingBox();
  if (!box || box.width < 64) throw new Error(`the icon renders at ${box?.width ?? 0}px`);

  await page.getByText(title.toUpperCase()).waitFor({ timeout: 5000 });

  for (const link of ["gilde.org", "GitHub", "Documentation"])
    await page.getByText(link, { exact: true }).waitFor({ timeout: 5000 });

  const hrefs = await page.locator("a").evaluateAll(links => links.map(a => a.getAttribute("href")));
  for (const expected of ["https://www.gilde.org", "https://github.com/fgilde/WebDataStudio",
    "https://fgilde.github.io/WebDataStudio/guide/"]) {
    if (!hrefs.includes(expected)) throw new Error(`no link to ${expected}: ${hrefs.join(", ")}`);
  }
} catch (e) { await fail("screen", e); }

// --- the provider, where the deployment configured one ----------------------------
try {
  const me = await (await page.request.get(`${baseUrl}/api/auth/me`)).json();
  const button = page.getByRole("link", { name: me.sso?.label ?? "Single sign-on" });

  if (me.sso?.enabled) {
    // A link rather than a fetch: a redirect cannot be followed out of an XMLHttpRequest.
    const href = await button.getAttribute("href");
    if (!href?.startsWith("/api/auth/sso"))
      throw new Error(`the provider button goes to ${href}`);

    // With accounts as well, both ways in are offered.
    if (!me.sso.only) await page.getByLabel("User").waitFor({ timeout: 5000 });
    console.log(`ok   the login screen offers ${me.sso.label}`);
  } else {
    if (await button.count() > 0) throw new Error("a provider button with no provider configured");
    console.log("ok   no provider configured, so no provider button");
  }
} catch (e) { await fail("sso", e); }

if (!(await page.title()).startsWith(title)) {
  await fail("tab", new Error(`the browser tab says "${await page.title()}"`));
}

// --- signing in leads to the studio, with the name in the bar -----------------------
try {
  await page.getByLabel("User").fill(user);
  await page.locator("input[type=password]").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();

  await page.getByAltText("WebDataStudio").first().waitFor({ timeout: 15000 });
  await page.getByText(title.toUpperCase()).waitFor({ timeout: 10000 });

  // The same two links live in the header, next to the theme button.
  await page.getByRole("link", { name: "Source on GitHub" }).waitFor({ timeout: 5000 });
  await page.getByRole("link", { name: "Documentation" }).waitFor({ timeout: 5000 });
} catch (e) { await fail("studio", e); }

await page.screenshot({ path: "smoke-login.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-login ok");
