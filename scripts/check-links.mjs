// Walks the documentation site and fails when a relative link points at a file that is not there.
// A renamed page must not silently break the sidebar.
import { readdir, readFile, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
import { dirname, join, normalize, resolve } from "node:path";

const root = resolve(process.argv[2] ?? "docs");
const rel = (path) => path.slice(root.length + 1).split("\\").join("/");
const problems = [];

async function files(directory) {
  const found = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    // The plans and specs are working documents, not part of the published site.
    if (entry.name === "superpowers" || entry.name === "node_modules") continue;

    const path = join(directory, entry.name);
    if (entry.isDirectory()) found.push(...(await files(path)));
    else if (/\.(md|html)$/.test(entry.name)) found.push(path);
  }
  return found;
}

const targets = (text) => [
  ...[...text.matchAll(/]\(([^)\s]+)\)/g)].map(m => m[1]),
  ...[...text.matchAll(/(?:href|src)="([^"]+)"/g)].map(m => m[1]),
];

for (const file of await files(root)) {
  const text = await readFile(file, "utf8");

  for (const raw of targets(text)) {
    if (/^(https?:|mailto:|data:|#)/.test(raw)) continue;

    const [path, anchor] = raw.split("#");
    if (!path) continue;

    // docsify resolves a leading slash against the docsify root, which is the folder its
    // index.html lives in.
    const base = path.startsWith("/") ? dirname(file) : dirname(file);
    const candidate = path.startsWith("/")
      ? join(docsifyRoot(file), path.slice(1))
      : join(base, path);

    const resolved = normalize(candidate);
    const exists = existsSync(resolved)
      || existsSync(join(resolved, "index.html"))
      || existsSync(join(resolved, "README.md"));

    if (!exists) problems.push(`${rel(file)} → ${raw}`);
    else if (anchor && resolved.endsWith(".md")) await checkAnchor(resolved, anchor, file, raw);
  }
}

function docsifyRoot(file) {
  // Every docsify tree has its own index.html; the nearest one upwards is the root of that tree.
  let directory = dirname(file);
  while (directory.startsWith(root)) {
    if (existsSync(join(directory, "index.html"))) return directory;
    directory = dirname(directory);
  }
  return root;
}

async function checkAnchor(target, anchor, from, raw) {
  const text = await readFile(target, "utf8");
  const slugs = [...text.matchAll(/^#{1,6}\s+(.+)$/gm)]
    .map(m => m[1].toLowerCase().replace(/[^\w\s-]/g, "").trim().replace(/\s+/g, "-"));

  if (!slugs.includes(anchor.toLowerCase())) problems.push(`${rel(from)} → ${raw} (no such heading)`);
}


if (problems.length > 0) {
  console.error(`broken links (${problems.length}):`);
  for (const problem of problems) console.error("  " + problem);
  process.exit(1);
}

console.log("all documentation links resolve");
