// Browser smoke test for the two things a unit test cannot see: activating a tool pane flashes its
// border even when the pane was already active, and the Ctrl+L chord applies a saved layout.
// Point BASE_URL at a running server (default http://localhost:5005).
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", e => errors.push(String(e)));

const check = (label, condition) => {
  console.log(`${condition ? "ok  " : "FAIL"} ${label}`);
  if (!condition) throw new Error(label);
};

await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 20000 });

const history = page.getByRole("button", { name: "History", exact: true });
const flashing = () => page.locator(".wds-flash").count();

// History is docked from the start, so the second click is the case the flash exists for: nothing
// else on screen moves, and without it the button looks broken.
await history.click();
check("first activation flashes", await flashing() === 1);
await page.waitForTimeout(900);
check("the flash clears itself", await flashing() === 0);
await history.click();
check("an already-active panel flashes again", await flashing() === 1);
await page.waitForTimeout(900);

// Ctrl+L opens the preset list; the number next to a preset is the key that applies it.
await page.keyboard.press("Control+l");
await page.getByText("Layout presets").waitFor({ timeout: 5000 });
await page.getByLabel("Name").fill("smoke");
await page.getByRole("button", { name: "Save current" }).click();
await page.waitForTimeout(400);
check("the first preset is slot 1", (await page.locator(".mantine-Badge-root").first().innerText()) === "1");
await page.keyboard.press("Escape");

// Close History, then bring the whole arrangement back with the chord.
const tab = page.getByRole("tab", { name: "History" });
await tab.hover();
await tab.locator("svg").first().click();
check("History is closed", await tab.count() === 0);

await page.keyboard.press("Control+l");
await page.keyboard.press("1");
await page.waitForTimeout(700);
check("Ctrl+L 1 restores the preset", await tab.count() === 1);

// Slot 0 is the default arrangement, the way back from a layout with every panel closed.
await page.keyboard.press("Control+l");
await page.keyboard.press("0");
await page.waitForTimeout(700);
const tabs = await page.getByRole("tab").allInnerTexts();
check(`Ctrl+L 0 rebuilds the default (${tabs.join(",")})`,
  ["Start", "Structure", "History", "Saved"].every(t => tabs.some(x => x.includes(t))));

// Clean up, so a second run starts from the same state.
await page.keyboard.press("Control+l");
await page.getByRole("button", { name: "Delete smoke" }).click();
await page.waitForTimeout(300);
await page.keyboard.press("Escape");

check("no console errors", errors.length === 0);
if (errors.length > 0) console.error(errors.slice(0, 5).join(" | "));
await browser.close();
console.log("layout smoke passed");
