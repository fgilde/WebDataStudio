// One release's section of CHANGELOG.md, for the release workflow to use as the body of the release.
//
// The workflow published only the generated commit list, and the changelog was pasted in afterwards
// by hand — a step that gets forgotten. Now the tag names the section:
//
//   node scripts/release-notes.mjs v1.3.0 > notes.md
//
// A tag with no section is not a reason to fail a release: nothing is written, and the workflow's
// generated notes are what the release gets. Anything worth saying goes to stderr, so stdout is the
// body and nothing else.
import { readFile } from "node:fs/promises";
import { argv } from "node:process";
import { pathToFileURL } from "node:url";

/// The lines between this version's heading and the next heading at the same level.
export function sectionOf(changelog, wanted) {
  const lines = changelog.split(/\r?\n/);
  const heading = /^##\s+v?(\S+)/;
  const version = wanted.replace(/^v/, "");

  let from = -1;
  let to = lines.length;

  for (const [index, line] of lines.entries()) {
    const match = heading.exec(line);
    if (!match) continue;

    if (from < 0) {
      if (match[1].replace(/^v/, "") === version) from = index + 1;
      continue;
    }

    to = index;
    break;
  }

  if (from < 0) return null;

  // The compare link at the end of a section is what the generated notes carry anyway, and two of
  // them under each other read like a mistake.
  const body = lines
    .slice(from, to)
    .join("\n")
    .replace(/\n+\*\*Full changelog\*\*:.*$/s, "")
    .trim();

  return body.length > 0 ? body : null;
}

// Only when run as a script: the function above is also a test's subject.
if (import.meta.url === pathToFileURL(argv[1] ?? "").href) {
  const wanted = argv[2] ?? "";
  const file = argv[3] ?? "CHANGELOG.md";

  if (wanted.length === 0) {
    console.error("usage: node scripts/release-notes.mjs <version> [changelog]");
    process.exit(2);
  }

  const text = await readFile(file, "utf8").catch(() => null);

  if (text === null) {
    console.error(`${file}: not here, so the generated notes are the notes`);
    process.exit(0);
  }

  const section = sectionOf(text, wanted);

  if (section === null) {
    console.error(`${file}: no section for ${wanted}, so the generated notes are the notes`);
    process.exit(0);
  }

  console.error(`${file}: ${section.split("\n").length} line(s) for ${wanted}`);
  process.stdout.write(section + "\n");
}
