// Browser check for signing in through an identity provider: the button, the round trip, and the
// role the studio gives somebody based on what the provider said about them.
//
// Needs a studio configured against a provider — the sample app host of
// Nextended.Aspire.Hosting.WebDataStudio brings a Keycloak with the demo realm imported, or run one
// by hand:
//
//   docker run -d --name kc -p 18081:8080 \
//     -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
//     -e KC_HOSTNAME=http://localhost:18081 -e KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true \
//     -v <Nextended>/Tests/TestProjects/WebDataStudio.AppHost/keycloak:/opt/keycloak/data/import \
//     quay.io/keycloak/keycloak:26.2 start-dev --import-realm
//
//   WDS_OIDC_AUTHORITY=http://localhost:18081/realms/webdatastudio \
//   WDS_OIDC_CLIENT_ID=webdatastudio WDS_OIDC_CLIENT_SECRET=studio-secret \
//   WDS_OIDC_REQUIRE_HTTPS=false WDS_OIDC_ADMINS=dba-group WDS_OIDC_EDITORS=developers \
//     dotnet run --project src/WebDataStudio.Server
//
// The realm's three people are the three roles: the demo account is in dba-group, bob in developers,
// and carol in neither, so she gets the default. All three share the demo password, which the realm
// file reads from the environment — WDS_SSO_ADMIN and WDS_SSO_PASSWORD if yours differ.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

const browser = await chromium.launch();

const me = await (await (await browser.newContext()).request.get(`${baseUrl}/api/auth/me`)).json();

if (!me.sso?.enabled) {
  console.log("skipped: this studio has no identity provider configured");
  await browser.close();
  process.exit(0);
}

// The demo realm's three people, and the password they share. The admin's name is the app host's
// `demo-user` parameter, so it is configurable here too.
const admin = process.env.WDS_SSO_ADMIN ?? "admin";
const secret = process.env.WDS_SSO_PASSWORD ?? "change-me-please";

/// One person, from the login screen to the studio. A fresh context each time: the point is the
/// sign-in, and a cookie from the last one would skip it.
const signIn = async (who, expected) => {
  const context = await browser.newContext({ viewport: { width: 1500, height: 950 } });
  const page = await context.newPage();
  const failures = [];

  page.on("response", r => { if (r.status() >= 500) failures.push(`${r.status()} ${r.url()}`); });

  await page.goto(baseUrl, { waitUntil: "networkidle" });

  // A link rather than a fetch: the provider answers with its own page, and a redirect cannot be
  // followed out of an XMLHttpRequest.
  await page.getByRole("link", { name: me.sso.label }).click();
  await page.waitForLoadState("networkidle");

  check(`${who} is asked by the provider (${new URL(page.url()).host})`,
    !page.url().startsWith(baseUrl));

  await page.fill("#username", who);
  await page.fill("#password", secret);
  await page.click("input[type=submit], button[type=submit]");
  await page.waitForLoadState("networkidle");

  check(`${who} lands back in the studio`, page.url().startsWith(baseUrl));

  const signed = await (await page.request.get(`${baseUrl}/api/auth/me`)).json();

  check(`${who} is signed in as ${signed.username}`, signed.authenticated === true);
  check(`${who} gets the ${expected} role (${signed.role})`, signed.role === expected);
  check(`nothing failed on the way (${failures.length})`, failures.length === 0);

  await context.close();
};

// The provider's groups decide the studio's role: dba-group is an admin, developers may write, and
// anybody in neither gets WDS_OIDC_DEFAULT_ROLE.
await signIn(admin, "admin");
await signIn("bob", "editor");
await signIn("carol", process.env.WDS_OIDC_DEFAULT_ROLE ?? "viewer");

await browser.close();
console.log("smoke-sso: done");
