// Rasterises the app icon into the PNGs a web app manifest needs. Playwright is already here for
// the browser checks, so this needs no image library: the SVG is rendered by a real browser at the
// size that is wanted.
//
//   node scripts/icons.mjs
import { chromium } from "playwright";
import { mkdir, readFile, writeFile } from "node:fs/promises";

const out = "public/icons";
await mkdir(out, { recursive: true });

const svg = await readFile("public/favicon.svg", "utf8");
const browser = await chromium.launch();

for (const size of [32, 192, 512]) {
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

// --- favicon.ico -------------------------------------------------------------------------------
// Windows takes a window's taskbar icon from an ICO, and Chromium asks for /favicon.ico by name.
// An ICO is a small header plus whole image files, and since Vista those may be PNGs — so the
// frames are packed rather than converted, and this needs no image library either.
const frames = [];

for (const size of [16, 32, 48]) {
  const frame = await browser.newPage({ viewport: { width: size, height: size } });
  await frame.setContent(
    `<!doctype html><style>html,body{margin:0;background:transparent}svg{display:block}</style>${
      svg.replace(/width="\d+"/, `width="${size}"`).replace(/height="\d+"/, `height="${size}"`)}`,
    { waitUntil: "load" });

  frames.push({ size, png: await frame.screenshot({ omitBackground: true }) });
  await frame.close();
}

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);            // reserved
header.writeUInt16LE(1, 2);            // 1 = icon
header.writeUInt16LE(frames.length, 4);

const directory = Buffer.alloc(frames.length * 16);
let offset = header.length + directory.length;

frames.forEach((frame, index) => {
  const entry = directory.subarray(index * 16, index * 16 + 16);
  entry.writeUInt8(frame.size, 0);     // width, where 0 would mean 256
  entry.writeUInt8(frame.size, 1);     // height
  entry.writeUInt8(0, 2);              // colours in the palette: none, it is a PNG
  entry.writeUInt8(0, 3);              // reserved
  entry.writeUInt16LE(1, 4);           // colour planes
  entry.writeUInt16LE(32, 6);          // bits per pixel
  entry.writeUInt32LE(frame.png.length, 8);
  entry.writeUInt32LE(offset, 12);
  offset += frame.png.length;
});

await writeFile("public/favicon.ico",
  Buffer.concat([header, directory, ...frames.map(frame => frame.png)]));

console.log(`favicon.ico (${frames.map(frame => frame.size).join(", ")})`);

await browser.close();
