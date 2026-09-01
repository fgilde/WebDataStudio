// node --test scripts/
//
// The release body comes out of this function, and a release is published once. So the cases that
// would be noticed too late are the ones written down: the wrong section, a heading with a `v`, the
// last section in the file, and a tag nobody wrote a section for.
import { test } from "node:test";
import assert from "node:assert/strict";
import { sectionOf } from "./release-notes.mjs";

const changelog = `# Changelog

Prose above the first release, which belongs to nobody.

## 1.3.0

What 1.3.0 did.

### A heading inside the section

- a bullet

**Full changelog**: https://example.invalid/compare/v1.2.0...v1.3.0

## v1.2.0

What 1.2.0 did.

## 1.1.0

The oldest one in this file.
`;

test("takes the section the tag names", () => {
  const section = sectionOf(changelog, "1.3.0");

  assert.match(section, /What 1\.3\.0 did\./);
  assert.match(section, /### A heading inside the section/);
  assert.doesNotMatch(section, /What 1\.2\.0 did\./);
});

test("the tag may carry its v, and so may the heading", () => {
  assert.match(sectionOf(changelog, "v1.3.0"), /What 1\.3\.0 did\./);
  assert.match(sectionOf(changelog, "1.2.0"), /What 1\.2\.0 did\./);
  assert.match(sectionOf(changelog, "v1.2.0"), /What 1\.2\.0 did\./);
});

test("leaves out the compare link the generated notes already carry", () => {
  assert.doesNotMatch(sectionOf(changelog, "1.3.0"), /Full changelog/);
});

test("reads the last section to the end of the file", () => {
  assert.equal(sectionOf(changelog, "1.1.0"), "The oldest one in this file.");
});

test("a version nobody wrote about is null, not an empty release body", () => {
  assert.equal(sectionOf(changelog, "9.9.9"), null);
  assert.equal(sectionOf("# Changelog\n\nnothing yet\n", "1.0.0"), null);
});

test("a heading with nothing under it is null as well", () => {
  assert.equal(sectionOf("## 2.0.0\n\n## 1.0.0\n\nsomething\n", "2.0.0"), null);
});
