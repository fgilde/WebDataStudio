// Rasterises the app icon into the PNGs a web app manifest needs. Playwright is already here for
// the browser checks, so this needs no image library: the SVG is rendered by a real browser at the
// size that is wanted.
//
//   node scripts/icons.mjs
import { chromium } from "playwright";
import { mkdir, readFile } from "node:fs/promises";

const out = "public/icons";
await mkdir(out, { recursive: true });

const svg = await readFile("public/favicon.svg", "utf8");
const browser = await chromium.launch();

for (const size of [192, 512]) {
  const page = await browser.newPage({
    viewport: { width: size, height: size },
    deviceScaleFactor: 1,
  });

  // A transparent background, so the icon keeps its own shape on any launcher.
  await page.setContent(
    `<!doctype html><style>html,body{margin:0;background:transparent}svg{display:block}</style>${
      svg.replace(/width="\d+"/, `width="${size}"`).replace(/height="\d+"/, `height="${size}"`)}`,
    { waitUntil: "load" });

  await page.screenshot({ path: `${out}/icon-${size}.png`, omitBackground: true });
  await page.close();
  console.log(`icon-${size}.png`);
}

// The maskable variant is the same drawing inside the safe area a launcher may crop to a circle.
const page = await browser.newPage({ viewport: { width: 512, height: 512 } });
await page.setContent(
  `<!doctype html><style>html,body{margin:0}
   .pad{width:512px;height:512px;display:grid;place-items:center;background:#0b1020}
   svg{width:400px;height:400px;display:block}</style>
   <div class="pad">${svg}</div>`, { waitUntil: "load" });

await page.screenshot({ path: `${out}/icon-maskable.png` });
console.log("icon-maskable.png");

await browser.close();
